using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NLCleaner.Services;

public static class RecoveryCommand
{
    public static async Task<int> RunAsync(bool dryRun, string expectedUser)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User?.Value ?? throw new InvalidOperationException("Не удалось определить пользователя.");
        var validatedUser = new SecurityIdentifier(expectedUser).Value;
        if (!dryRun && !Elevation.IsAdministrator)
        {
            try
            {
                var start = new ProcessStartInfo(DesktopPreferences.ExecutablePath)
                {
                    UseShellExecute = true, Verb = "runas",
                    Arguments = "--restore-and-exit --recovery-user " + validatedUser
                };
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить восстановление.");
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return 5; }
        }
        if (!string.Equals(currentUser, validatedUser, StringComparison.Ordinal))
            throw new InvalidOperationException("Для восстановления выбрана другая учётная запись. Войдите под исходным пользователем; удаление отменено.");

        return await Task.Run(() =>
        {
            using var mutex = new Mutex(false, dryRun ? @"Local\AstraLoader-RecoveryPreview" : @"Global\NL-Cleaner-Operation");
            var owned = false;
            try
            {
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new InvalidOperationException("Другая операция ещё выполняется. Закройте Astra Loader и повторите удаление.");
                var result = new CleanerEngine(dryRun, entry => Console.WriteLine(entry), (_, _) => { })
                    .Run(true, CancellationToken.None);
                return result.Errors == 0 && !result.Cancelled ? 0 : 1;
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        });
    }
}
