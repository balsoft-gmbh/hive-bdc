using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Windows-service installer/uninstaller. Run the exe with `install` / `uninstall`
/// (optionally `install --dry-run`). Interactive by default; `--quiet` runs
/// unattended (no prompts, no UAC relaunch, no pause).
///
/// The installer ONLY registers the Windows service and its run-as account. It does
/// NOT collect input folders, SharePoint or the per-company column mapping — those are
/// configured after setup in the dashboard (Settings), per company (dataAreaId), and
/// persisted to appsettings.json by the service. So a non-LocalSystem run-as account is
/// granted Modify on appsettings.json here, otherwise the dashboard could not save
/// (the file lives under Program Files).
///
/// Quiet flags: --account, --password, --service-name, --display-name, --start /
/// --no-start. A blank/omitted --account installs as LocalSystem.
/// </summary>
public static class Installer
{
    const string DefaultServiceName = "HiveBDC";
    const string DefaultDisplayName = "Hive BDC, by Balsoft";
    const string SettingsSection = SettingsFile.Section;

    public static int RunInstall(string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        return HasFlag(args, "--quiet") ? RunQuietInstall(args, dryRun) : RunInteractiveInstall(dryRun);
    }

    public static int RunUninstall(string[] args)
        => HasFlag(args, "--quiet") ? RunQuietUninstall(args) : RunInteractiveUninstall();

    static int RunInteractiveInstall(bool dryRun)
    {
        if (!dryRun && !IsElevated())
        {
            // Try to self-elevate (UAC prompt) so a double-clicked setup just works.
            if (TryRelaunchElevated())
                return 0;
            Console.Error.WriteLine("This requires administrator rights. Re-run this from an elevated/Administrator command prompt.");
            return Done(1);
        }

        try
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running exe path.");
            string exeDir = Path.GetDirectoryName(exePath)!;
            string settingsPath = Path.Combine(exeDir, "appsettings.json");

            Console.WriteLine("=== Hive BDC, by Balsoft — service install ===");
            if (dryRun) Console.WriteLine("(dry-run: no service is created; the sc.exe commands are printed instead)");
            Console.WriteLine();
            Console.WriteLine("This registers the Windows service. Input folders, SharePoint export and the");
            Console.WriteLine("per-company mapping are configured afterwards in the dashboard (Settings).");
            Console.WriteLine();
            Console.WriteLine("Run the service as:");
            Console.WriteLine("  1) LocalSystem            (simplest; NO access to network/UNC shares)");
            Console.WriteLine("  2) Domain/specific account (required for \\\\server\\share paths)");
            string accChoice = Prompt("Choose 1 or 2", "1");

            string? account = null, password = null;
            if (accChoice.Trim() == "2")
            {
                account = Prompt(@"Account (e.g. DOMAIN\svc-hivebdc)", "");
                password = PromptPassword("Password");
            }

            string serviceName = Prompt("Service name", DefaultServiceName);
            string displayName = Prompt("Display name", DefaultDisplayName);

            EnsureSettingsExist(settingsPath);

            var (createArgs, failureArgs) = BuildScArgs(exePath, serviceName, displayName, account, password);

            if (dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("Would run:");
                Console.WriteLine("  sc.exe " + RenderArgs(createArgs, maskPassword: true));
                Console.WriteLine("  sc.exe " + RenderArgs(failureArgs, maskPassword: false));
                Console.WriteLine();
                Console.WriteLine("(dry-run complete — no service created)");
                return Done(0);
            }

            StopAndDeleteIfPresent(serviceName);

            var (createCode, createOut) = RunSc(createArgs);
            Console.WriteLine(createOut.Trim());
            if (createCode != 0)
            {
                Console.Error.WriteLine("sc create failed (see output above).");
                return Done(createCode);
            }

            var (_, failOut) = RunSc(failureArgs);
            Console.WriteLine(failOut.Trim());

            // Grant the run-as account "Log on as a service" (sc create does NOT do this —
            // it's the usual cause of start error 1069) and write access to appsettings.json
            // (so the dashboard's Settings can save). LocalSystem needs neither.
            if (account is not null && OperatingSystem.IsWindows())
            {
                var (granted, gmsg) = ServiceAccount.GrantLogonAsService(account);
                Console.WriteLine(granted
                    ? "  " + gmsg
                    : "  WARNING: " + gmsg + Environment.NewLine +
                      "  Grant it manually: services.msc -> service -> Properties -> Log On tab -> re-enter the account + password.");
                GrantSettingsWrite(account, settingsPath);
            }

            Console.WriteLine();
            int startCode = 0;
            string start = Prompt("Start the service now? [Y/n]", "Y");
            if (!start.StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                string startOut;
                (startCode, startOut) = RunSc(new List<string> { "start", serviceName });
                Console.WriteLine(startOut.Trim());
                var (_, queryOut) = RunSc(new List<string> { "query", serviceName });
                foreach (var line in queryOut.Split('\n'))
                    if (line.Contains("STATE")) Console.WriteLine("  " + line.Trim());
                if (startCode != 0)
                    Console.Error.WriteLine("Service was created but failed to START. Check the account's \"Log on as a service\" right.");
            }

            PrintSummary(serviceName, account ?? "LocalSystem", settingsPath);
            return Done(startCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Install failed: " + ex.Message);
            Console.Error.WriteLine(ex.ToString());
            return Done(1);
        }
    }

    /// <summary>Pause before returning so a double-clicked window doesn't vanish before the result can be read.</summary>
    static int Done(int code)
    {
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close...");
            try { Console.ReadLine(); } catch { /* no interactive console */ }
        }
        return code;
    }

    static int RunInteractiveUninstall()
    {
        if (!IsElevated())
        {
            if (TryRelaunchElevatedUninstall()) return 0;
            Console.Error.WriteLine("This requires administrator rights. Re-run this from an elevated/Administrator command prompt.");
            return Done(1);
        }

        string serviceName = Prompt("Service name to remove", DefaultServiceName);

        var (_, stopOut) = RunSc(new List<string> { "stop", serviceName });
        Console.WriteLine(stopOut.Trim());

        // Wait for the service to fully stop so `delete` doesn't leave it
        // "marked for deletion" (lingering until every handle closes / reboot).
        WaitForStopped(serviceName, timeoutSeconds: 30);

        var (delCode, delOut) = RunSc(new List<string> { "delete", serviceName });
        Console.WriteLine(delOut.Trim());

        if (delCode == 0) Console.WriteLine($"Service '{serviceName}' removed.");
        return Done(delCode);
    }

    // ── quiet (unattended) mode: no prompts, no UAC relaunch, no pause ──────

    static int RunQuietInstall(string[] args, bool dryRun)
    {
        if (!dryRun && !IsElevated())
        {
            Console.Error.WriteLine("install --quiet requires an elevated process (no UAC relaunch in quiet mode). Run it from an elevated context.");
            return 1;
        }

        try
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running exe path.");
            string exeDir = Path.GetDirectoryName(exePath)!;
            string settingsPath = Path.Combine(exeDir, "appsettings.json");

            string? account = GetValue(args, "--account");
            string? password = account is null ? null : GetValue(args, "--password");
            string serviceName = GetValue(args, "--service-name") ?? DefaultServiceName;
            string displayName = GetValue(args, "--display-name") ?? DefaultDisplayName;
            bool start = !HasFlag(args, "--no-start");

            EnsureSettingsExist(settingsPath);

            var (createArgs, failureArgs) = BuildScArgs(exePath, serviceName, displayName, account, password);

            // A genuine account CHANGE requires both an account and a non-empty password;
            // otherwise an existing service is upgraded in place, keeping its run-as account.
            bool changingAccount = account is not null && !string.IsNullOrEmpty(password);
            bool upgradeInPlace = ServiceInstalled(serviceName) && !changingAccount;

            if (dryRun)
            {
                if (upgradeInPlace)
                {
                    Console.WriteLine("Would UPGRADE IN PLACE (keep the existing run-as account):");
                    Console.WriteLine($"  sc.exe stop {serviceName}");
                    Console.WriteLine($"  sc.exe config {serviceName} binPath= \"{exePath}\"");
                }
                else
                {
                    Console.WriteLine("Would run:");
                    Console.WriteLine("  sc.exe " + RenderArgs(createArgs, maskPassword: true));
                }
                Console.WriteLine("  sc.exe " + RenderArgs(failureArgs, maskPassword: false));
                Console.WriteLine("(dry-run complete - no changes made)");
                return 0;
            }

            if (upgradeInPlace)
            {
                // Upgrade preserving the run-as account: Windows stores the account's password
                // (in LSA), so it is NOT re-supplied on an upgrade / auto-update. Stop, retarget
                // the (replaced) binary, restart — no delete/recreate, so no password is needed.
                // appsettings.json and its ACL grant are left intact by the installer's [Files] step.
                Console.WriteLine($"Service '{serviceName}' exists - upgrading in place, keeping its run-as account.");
                var (_, stopOut) = RunSc(new List<string> { "stop", serviceName });
                Console.WriteLine("Stopping: " + stopOut.Trim());
                WaitForStopped(serviceName, timeoutSeconds: 30);
                var (cfgCode, cfgOut) = RunSc(new List<string> { "config", serviceName, "binPath=", exePath });
                Console.WriteLine("Config: " + cfgOut.Trim());
                if (cfgCode != 0)
                {
                    Console.Error.WriteLine($"sc config failed (exit code {cfgCode}, see output above).");
                    return cfgCode;
                }
                RunSc(failureArgs);
            }
            else
            {
                StopAndDeleteIfPresent(serviceName);

                var (createCode, createOut) = RunSc(createArgs);
                Console.WriteLine("Creating: " + createOut.Trim());
                if (createCode != 0)
                {
                    Console.Error.WriteLine($"sc create failed (exit code {createCode}, see output above).");
                    return createCode;
                }

                var (_, failOut) = RunSc(failureArgs);
                Console.WriteLine("Failure actions: " + failOut.Trim());

                if (account is not null && OperatingSystem.IsWindows())
                {
                    var (granted, gmsg) = ServiceAccount.GrantLogonAsService(account);
                    if (granted)
                        Console.WriteLine(gmsg);
                    else
                        Console.Error.WriteLine("WARNING: " + gmsg + " Grant \"Log on as a service\" manually via services.msc -> Properties -> Log On.");
                    GrantSettingsWrite(account, settingsPath);
                }
            }

            int startCode = 0;
            if (start)
            {
                string startOut;
                (startCode, startOut) = RunSc(new List<string> { "start", serviceName });
                Console.WriteLine("Starting: " + startOut.Trim());
                if (startCode != 0)
                    Console.Error.WriteLine("Service could not START. Check the run-as account's \"Log on as a service\" right and access to the configured folders/share.");
            }

            Console.WriteLine(upgradeInPlace
                ? $"Upgraded service={serviceName} (run-as account kept) settings={settingsPath}"
                : $"Installed service={serviceName} account={account ?? "LocalSystem"} settings={settingsPath}");
            return startCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Install failed: " + ex.Message);
            return 1;
        }
    }

    static int RunQuietUninstall(string[] args)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("uninstall --quiet requires an elevated process (no UAC relaunch in quiet mode). Run it from an elevated context.");
            return 1;
        }

        try
        {
            string serviceName = GetValue(args, "--service-name") ?? DefaultServiceName;

            // Idempotent: a missing service is a success, not an error.
            var (queryCode, queryOut) = RunSc(new List<string> { "query", serviceName });
            if (queryCode != 0 || queryOut.Contains("1060"))
            {
                Console.WriteLine($"Service '{serviceName}' is not installed - nothing to do.");
                return 0;
            }

            var (_, stopOut) = RunSc(new List<string> { "stop", serviceName });
            Console.WriteLine("Stopping: " + stopOut.Trim());
            WaitForStopped(serviceName, timeoutSeconds: 30);

            var (delCode, delOut) = RunSc(new List<string> { "delete", serviceName });
            Console.WriteLine("Deleting: " + delOut.Trim());
            if (delCode != 0)
            {
                Console.Error.WriteLine($"sc delete failed (exit code {delCode}, see output above).");
                return delCode;
            }

            Console.WriteLine($"Service '{serviceName}' removed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Uninstall failed: " + ex.Message);
            return 1;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Value of a `--flag value` pair, or null when the flag (or its value) is absent.</summary>
    static string? GetValue(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Ensure an appsettings.json exists next to the exe. The installer's [Files] step
    /// ships the default template (onlyifdoesntexist); this is a fallback for a bare CLI
    /// install with no file present. Writes an empty Companies[] — everything is then
    /// configured in the dashboard (Settings). Never overwrites an existing file.
    /// </summary>
    static void EnsureSettingsExist(string path)
    {
        if (File.Exists(path)) return;
        var root = new JsonObject
        {
            [SettingsSection] = new JsonObject
            {
                ["PollSeconds"] = 30,
                ["LogDir"] = "",
                ["License"] = new JsonObject { ["File"] = "" },
                ["SharePointAuth"] = new JsonObject { ["TenantId"] = "", ["ClientId"] = "", ["ClientSecret"] = "" },
                ["DocumentTypes"] = new JsonArray(),   // built-in types are added on load (without company entries); configured in the dashboard
            },
            ["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject
                {
                    ["Default"] = "Information",
                    ["Microsoft.Hosting.Lifetime"] = "Information",
                    ["Microsoft"] = "Warning",
                },
            },
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote default {path}");
    }

    /// <summary>
    /// Grant the run-as account Modify on appsettings.json so the dashboard's Settings
    /// (written by the service over the loopback API) can persist — the file lives under
    /// Program Files, which a non-admin service account cannot otherwise write. LocalSystem
    /// needs no grant. Best-effort: a failure warns and points at the manual fix.
    /// </summary>
    static void GrantSettingsWrite(string account, string settingsPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(settingsPath)) return;
        try
        {
            var (code, output) = RunProcess("icacls", new List<string> { settingsPath, "/grant", account + ":(M)" });
            Console.WriteLine(code == 0
                ? $"  Granted {account} write access to appsettings.json (dashboard Settings can save)."
                : $"  WARNING: could not grant {account} write to appsettings.json — the dashboard's Save may fail." +
                  Environment.NewLine + "  Grant it manually (icacls) or in Explorer: appsettings.json -> Properties -> Security -> Modify. " + output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("  WARNING: icacls grant failed: " + ex.Message);
        }
    }

    /// <summary>True when the named service is registered (sc query succeeds and isn't 1060).</summary>
    static bool ServiceInstalled(string serviceName)
    {
        var (code, output) = RunSc(new List<string> { "query", serviceName });
        return code == 0 && !output.Contains("1060");
    }

    /// <summary>Stop + delete an existing service so it can be recreated (account change / fresh).</summary>
    static void StopAndDeleteIfPresent(string serviceName)
    {
        var (queryCode, queryOut) = RunSc(new List<string> { "query", serviceName });
        if (queryCode != 0 || queryOut.Contains("1060")) return;   // not installed

        Console.WriteLine($"Service '{serviceName}' already exists - upgrading (stop, delete, recreate).");
        var (_, stopOut) = RunSc(new List<string> { "stop", serviceName });
        Console.WriteLine("Stopping: " + stopOut.Trim());
        WaitForStopped(serviceName, timeoutSeconds: 30);
        var (_, delOut) = RunSc(new List<string> { "delete", serviceName });
        Console.WriteLine("Deleting: " + delOut.Trim());
        WaitForStopped(serviceName, timeoutSeconds: 10);
    }

    /// <summary>
    /// Build sc.exe argument lists. sc.exe requires a SPACE after each `key=`,
    /// which ProcessStartInfo.ArgumentList produces by passing `key=` and its
    /// value as two separate tokens.
    /// </summary>
    static (List<string> create, List<string> failure) BuildScArgs(
        string exePath, string serviceName, string displayName, string? account, string? password)
    {
        var createArgs = new List<string>
        {
            "create", serviceName,
            "binPath=", exePath,
            "start=", "auto",
            "DisplayName=", displayName,
        };
        if (account is not null)
        {
            createArgs.Add("obj="); createArgs.Add(account);
            createArgs.Add("password="); createArgs.Add(password ?? "");
        }
        else
        {
            createArgs.Add("obj="); createArgs.Add("LocalSystem");
        }
        var failureArgs = new List<string>
        {
            "failure", serviceName,
            "reset=", "86400",
            "actions=", "restart/5000/restart/5000/restart/10000",
        };
        return (createArgs, failureArgs);
    }

    static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static bool TryRelaunchElevated() => TryRelaunchElevated("install");
    static bool TryRelaunchElevatedUninstall() => TryRelaunchElevated("uninstall");

    /// <summary>Relaunch this exe elevated (UAC) to run the given command. Returns false if it couldn't (e.g. user declined).</summary>
    static bool TryRelaunchElevated(string command)
    {
        try
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running exe path.");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = command,
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false; // user declined UAC, or no shell available
        }
    }

    static (int code, string output) RunSc(List<string> args) => RunProcess("sc.exe", args);

    static (int code, string output) RunProcess(string fileName, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        string combined = (stdout + stderr).Trim();
        return (p.ExitCode, combined.Length == 0 ? "(no output)" : combined);
    }

    /// <summary>Poll sc until the service reports STOPPED (or is gone), so `delete` is clean.</summary>
    static void WaitForStopped(string serviceName, int timeoutSeconds)
    {
        for (int i = 0; i < timeoutSeconds * 2; i++)   // poll every 500 ms
        {
            var (code, output) = RunSc(new List<string> { "query", serviceName });
            if (code != 0 || output.Contains("1060")) return;   // service not found / gone
            if (output.Contains("STOPPED")) return;
            Thread.Sleep(500);
        }
    }

    /// <summary>Render an sc.exe arg list the way it actually runs (space after `key=`, quoted values).</summary>
    static string RenderArgs(List<string> args, bool maskPassword)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Count; i++)
        {
            string tok = args[i];
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(tok);
            if (tok.EndsWith('=') && i + 1 < args.Count)
            {
                string val = args[++i];
                if (maskPassword && tok.Equals("password=", StringComparison.OrdinalIgnoreCase))
                    val = "***";
                sb.Append(' ');
                bool quote = val.Length == 0 || val.Contains(' ');
                sb.Append(quote ? $"\"{val}\"" : val);
            }
        }
        return sb.ToString();
    }

    static void PrintSummary(string service, string account, string settingsPath)
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────");
        Console.WriteLine($" Service  : {service}");
        Console.WriteLine($" Run as   : {account}");
        Console.WriteLine($" Settings : {settingsPath}");
        Console.WriteLine("──────────────────────────────────────────────");
        Console.WriteLine(" Next: open the dashboard and configure companies (input folder, SharePoint,");
        Console.WriteLine("       column mapping) per dataAreaId under Settings, then install the license");
        Console.WriteLine("       (Renew license…), or drop it at %ProgramData%\\HiveBDC\\license.lic.");
        Console.WriteLine(" Check:  sc query " + service + "   (or services.msc)");
        if (account != "LocalSystem")
            Console.WriteLine(" Note :  ensure the account has \"Log on as a service\" + access to the configured folders/share.");
        Console.WriteLine();
    }

    // ── prompt helpers (fall back to ReadLine when stdin is redirected) ───────

    static string Prompt(string label, string def)
    {
        Console.Write(string.IsNullOrEmpty(def) ? $"{label}: " : $"{label} [{def}]: ");
        string? line = Console.ReadLine();
        if (line is null) return def;
        line = line.Trim();
        if (line.Length == 0) return def;       // Enter keeps default (or "" when def is "")
        return line;
    }

    static string PromptPassword(string label)
    {
        Console.Write($"{label}: ");
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";    // can't mask redirected input

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        return sb.ToString();
    }
}
