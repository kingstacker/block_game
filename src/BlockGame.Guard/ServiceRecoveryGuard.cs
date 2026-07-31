using System.Diagnostics;
using System.Text;

namespace BlockGame.Guard;

/// <summary>
/// 守护服务对自身的自愈:周期性重刷 Windows 服务恢复(failure)配置,
/// 防止有人用 <c>sc failure BlockGameGuard actions= "" reset= 0</c> 清空恢复动作
/// 后再停止服务,从而绕过“被停即崩溃并重启”的机制。只要服务进程还活着,
/// 就持续保证恢复策略在位。参数需与 GuardServiceInstaller.ConfigureRecovery 保持一致。
/// </summary>
internal static class ServiceRecoveryGuard
{
    private const string ServiceName = "BlockGameGuard";

    public static void Reapply()
    {
        // 恢复动作:首次失败 0.5s 后重启,其后 1s、3s;失败计数 24 小时后清零。
        RunSc(
            "failure",
            ServiceName,
            "actions=",
            "restart/500/restart/1000/restart/3000",
            "reset=",
            "86400");
        // 让“带非零退出码的停止”也触发恢复动作,配合宿主的 FailFast。
        RunSc("failureflag", ServiceName, "1");
    }

    private static void RunSc(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            // 读干输出避免管道阻塞,但看门狗本身绝不能因此崩溃。
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            // 重刷恢复配置是尽力而为;失败时下一轮再试,不影响主拦截循环。
        }
    }
}
