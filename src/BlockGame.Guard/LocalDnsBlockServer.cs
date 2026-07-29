using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BlockGame.Core.Services;

namespace BlockGame.Guard;

internal sealed record WebsiteBlockRegistration(
    string Domain,
    string RuleId,
    string RuleName);

internal sealed class LocalDnsBlockServer : IAsyncDisposable
{
    private readonly Action<WebsiteBlockRegistration, string> _onBlocked;
    private readonly Action<Exception> _onError;
    private readonly object _registrationsLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _workers = [];
    private IReadOnlyList<WebsiteBlockRegistration> _registrations = [];
    private UdpClient? _udp4;
    private UdpClient? _udp6;
    private TcpListener? _tcp4;
    private TcpListener? _tcp6;

    public LocalDnsBlockServer(
        Action<WebsiteBlockRegistration, string> onBlocked,
        Action<Exception> onError)
    {
        _onBlocked = onBlocked;
        _onError = onError;
    }

    public void Start()
    {
        if (_workers.Count > 0)
        {
            return;
        }

        try
        {
            _udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
            _tcp4 = new TcpListener(IPAddress.Loopback, 53);
            _tcp4.Start();

            _udp6 = new UdpClient(AddressFamily.InterNetworkV6);
            _udp6.Client.DualMode = false;
            _udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 53));
            _tcp6 = new TcpListener(IPAddress.IPv6Loopback, 53);
            _tcp6.Server.DualMode = false;
            _tcp6.Start();

            _workers.Add(RunUdpAsync(_udp4, _stop.Token));
            _workers.Add(RunUdpAsync(_udp6, _stop.Token));
            _workers.Add(RunTcpAsync(_tcp4, _stop.Token));
            _workers.Add(RunTcpAsync(_tcp6, _stop.Token));
        }
        catch
        {
            DisposeSockets();
            throw;
        }
    }

    public void UpdateRegistrations(IReadOnlyList<WebsiteBlockRegistration> registrations)
    {
        lock (_registrationsLock)
        {
            _registrations = registrations.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        DisposeSockets();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or ObjectDisposedException
                or SocketException)
        {
            // Socket disposal is how the asynchronous receive loops are stopped.
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task RunUdpAsync(UdpClient listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult request = await listener
                    .ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                byte[]? response = BuildBlockedResponse(request.Buffer);
                if (response is not null)
                {
                    await listener
                        .SendAsync(response, request.RemoteEndPoint, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (
                cancellationToken.IsCancellationRequested
                && exception is OperationCanceledException
                    or ObjectDisposedException
                    or SocketException)
            {
                break;
            }
            catch (Exception exception)
            {
                _onError(exception);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunTcpAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = HandleTcpClientAsync(client, cancellationToken);
            }
            catch (Exception exception) when (
                cancellationToken.IsCancellationRequested
                && exception is OperationCanceledException
                    or ObjectDisposedException
                    or SocketException)
            {
                break;
            }
            catch (Exception exception)
            {
                _onError(exception);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleTcpClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var lengthBytes = new byte[2];
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await TryReadExactlyAsync(stream, lengthBytes, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }

                    int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                    if (length is < 12 or > ushort.MaxValue)
                    {
                        return;
                    }

                    var request = new byte[length];
                    if (!await TryReadExactlyAsync(stream, request, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }

                    byte[]? response = BuildBlockedResponse(request);
                    if (response is null)
                    {
                        return;
                    }

                    BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)response.Length);
                    await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or OperationCanceledException
                    or ObjectDisposedException
                    or SocketException)
            {
                // One malformed or disconnected DNS client must not stop the listener.
                if (!cancellationToken.IsCancellationRequested)
                {
                    _onError(exception);
                }
            }
        }
    }

    private byte[]? BuildBlockedResponse(byte[] request)
    {
        if (!DnsMessageResponder.TryCreateNameErrorResponse(
                request,
                out string queryDomain,
                out byte[] response))
        {
            _onError(new InvalidDataException(
                $"收到无法解析的DNS查询，长度 {request.Length} 字节。"));
            return null;
        }

        WebsiteBlockRegistration? match = FindRegistration(queryDomain);
        if (match is not null)
        {
            _onBlocked(match, queryDomain);
        }

        return response;
    }

    private WebsiteBlockRegistration? FindRegistration(string queryDomain)
    {
        IReadOnlyList<WebsiteBlockRegistration> snapshot;
        lock (_registrationsLock)
        {
            snapshot = _registrations;
        }

        return snapshot
            .OrderByDescending(registration => registration.Domain.Length)
            .FirstOrDefault(registration =>
                BlockGame.Core.Services.WebsiteDomainRules.IsMatch(
                    queryDomain,
                    registration.Domain));
    }

    private static async Task<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream
                .ReadAsync(buffer[read..], cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private void DisposeSockets()
    {
        _udp4?.Dispose();
        _udp6?.Dispose();
        _tcp4?.Stop();
        _tcp6?.Stop();
        _udp4 = null;
        _udp6 = null;
        _tcp4 = null;
        _tcp6 = null;
    }
}
