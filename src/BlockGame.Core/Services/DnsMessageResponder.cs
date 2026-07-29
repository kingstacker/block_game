using System.Buffers.Binary;
using System.Text;

namespace BlockGame.Core.Services;

public static class DnsMessageResponder
{
    public static bool TryCreateNameErrorResponse(
        ReadOnlySpan<byte> request,
        out string queryDomain,
        out byte[] response)
    {
        queryDomain = string.Empty;
        response = [];
        if (request.Length < 17
            || (request[2] & 0x80) != 0
            || (request[2] & 0x78) != 0)
        {
            return false;
        }

        ushort questionCount = BinaryPrimitives.ReadUInt16BigEndian(request[4..6]);
        if (questionCount != 1)
        {
            return false;
        }

        int offset = 12;
        var labels = new List<string>();
        while (offset < request.Length)
        {
            int labelLength = request[offset++];
            if (labelLength == 0)
            {
                break;
            }

            if ((labelLength & 0xC0) != 0
                || labelLength > 63
                || offset + labelLength > request.Length)
            {
                return false;
            }

            labels.Add(Encoding.ASCII.GetString(request.Slice(offset, labelLength)));
            offset += labelLength;
        }

        if (labels.Count == 0 || offset + 4 > request.Length)
        {
            return false;
        }

        queryDomain = string.Join('.', labels).TrimEnd('.').ToLowerInvariant();
        int questionEnd = offset + 4;
        response = request[..questionEnd].ToArray();
        response[2] = (byte)(0x80 | (request[2] & 0x01));
        response[3] = 0x83;
        Array.Clear(response, 6, 6);
        return true;
    }
}
