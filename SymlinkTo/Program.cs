using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SymlinkTo;

static class Program
{
    public const string MenuTitle = "Create symlink in...";
    public const string RemoveTitle = "Delete symlink";
    public const string AppTitle = "SymlinkTo";

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Ui.Info("Usage:\n  symlinkto install | uninstall\n  symlinkto <file or folder> [...]");
            return 0;
        }

        switch (args[0])
        {
            case "install": return Installer.Install();
            case "uninstall": return Installer.Uninstall();
            case "--create": return Linker.RunElevatedJob(args.Skip(1).ToList());
            case "--remove":
                {
                    var links = args.Skip(1).Select(Path.GetFullPath).ToList();
                    if (OperatingSystem.IsWindows())
                    {
                        var batch = WinBatch.Collect("remove", links);
                        if (batch == null) return 0;
                        links = batch;
                    }
                    return Linker.Remove(links);
                }
        }

        var sources = args.Select(Path.GetFullPath).ToList();

        if (OperatingSystem.IsWindows())
        {
            var batch = WinBatch.Collect("create", sources);
            if (batch == null) return 0;
            sources = batch;
        }

        return Linker.Handle(sources);
    }
}

record Item(string Target, string Link);

static class Linker
{
    public static int Handle(List<string> sources)
    {
        var missing = sources.Where(s => !Exists(s)).ToList();
        sources = sources.Except(missing).ToList();
        if (sources.Count == 0)
        {
            Ui.Error("Files not found:\n" + string.Join("\n", missing));
            return 1;
        }

        var title = sources.Count == 1
            ? $"Where to put the symlink to '{Path.GetFileName(sources[0])}'?"
            : $"Where to put the symlinks ({sources.Count} items)?";

        var dest = Ui.PickFolder(title, Settings.LoadLastDir());
        if (dest == null) return 0;
        dest = Path.GetFullPath(dest);
        Settings.SaveLastDir(dest);

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = sources.Select(s => new Item(s, UniqueLink(dest, s, reserved))).ToList();

        var (retry, errors) = Create(items);
        errors.InsertRange(0, missing.Select(m => $"Not found: {m}"));

        if (retry.Count > 0)
        {
            if (!Elevate(retry))
                errors.Add("Creating symlinks on Windows requires administrator rights or Developer Mode enabled. Operation cancelled.");
        }

        if (errors.Count > 0)
        {
            Ui.Error(string.Join("\n", errors));
            return 1;
        }
        return 0;
    }

    public static int Remove(List<string> paths)
    {
        int removed = 0;
        var notLinks = new List<string>();
        var errors = new List<string>();

        foreach (var p in paths.Distinct())
        {
            try
            {
                if (!IsSymlink(p)) { notLinks.Add(Path.GetFileName(p)); continue; }
                DeleteLink(p);
                removed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(p)}: {ex.Message}");
            }
        }

        if (errors.Count > 0) Ui.Error(string.Join("\n", errors));
        if (notLinks.Count > 0) Ui.Info("Not a symlink, skipped (nothing was deleted):\n" + string.Join("\n", notLinks));
        return errors.Count > 0 ? 1 : 0;
    }

    static bool IsSymlink(string p) => new FileInfo(p).LinkTarget != null || new DirectoryInfo(p).LinkTarget != null;

    static void DeleteLink(string p)
    {
        if (OperatingSystem.IsWindows() && File.GetAttributes(p).HasFlag(FileAttributes.Directory))
            Directory.Delete(p, recursive: false);
        else
            File.Delete(p);
    }

    public static int RunElevatedJob(List<string> pairs)
    {
        var items = new List<Item>();
        for (int i = 0; i + 1 < pairs.Count; i += 2) items.Add(new Item(pairs[i], pairs[i + 1]));
        var (_, errors) = Create(items);
        if (errors.Count > 0) { Ui.Error(string.Join("\n", errors)); return 1; }
        return 0;
    }

    static (List<Item> retry, List<string> errors) Create(List<Item> items)
    {
        var retry = new List<Item>();
        var errors = new List<string>();
        foreach (var it in items)
        {
            try
            {
                if (Directory.Exists(it.Target)) Directory.CreateSymbolicLink(it.Link, it.Target);
                else File.CreateSymbolicLink(it.Link, it.Target);
            }
            catch (Exception ex) when (NeedsPrivilege(ex))
            {
                retry.Add(it);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(it.Target)}: {ex.Message}");
            }
        }
        return (retry, errors);
    }

    static bool NeedsPrivilege(Exception ex) =>
        OperatingSystem.IsWindows() && !IsAdmin() &&
        (ex is UnauthorizedAccessException || (ex is IOException && (ex.HResult & 0xFFFF) == 1314));

    [SupportedOSPlatform("windows")]
    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static bool Elevate(List<Item> items)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var args = "--create " + string.Join(" ", items.SelectMany(i => new[] { Quote(i.Target), Quote(i.Link) }));
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    static bool ExistsOrLink(string p) => Exists(p) || new FileInfo(p).LinkTarget != null;

    static string UniqueLink(string dir, string src, HashSet<string> reserved)
    {
        var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.Length == 0) name = "link";

        var isDir = Directory.Exists(src);
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        if (stem.Length == 0) { stem = name; ext = ""; }

        var candidate = Path.Combine(dir, name);
        for (int n = 1; ExistsOrLink(candidate) || reserved.Contains(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");

        reserved.Add(candidate);
        return candidate;
    }

    static string Quote(string a)
    {
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (var c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') sb.Append('\\', bs * 2 + 1).Append('"');
            else sb.Append('\\', bs).Append(c);
            bs = 0;
        }
        return sb.Append('\\', bs * 2).Append('"').ToString();
    }
}

[SupportedOSPlatform("windows")]
static class WinBatch
{
    public static List<string>? Collect(string key, List<string> mine)
    {
        var dir = Path.Combine(Path.GetTempPath(), "symlinkto-queue-" + key);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, Guid.NewGuid() + ".txt"), mine);

        using var mutex = new Mutex(false, @"Local\SymlinkTo.Leader." + key);
        if (!mutex.WaitOne(0)) return null;
        try
        {
            Thread.Sleep(600);
            int last = -1;
            for (int quiet = 0; quiet < 2; Thread.Sleep(250))
            {
                int n = Directory.GetFiles(dir).Length;
                if (n == last) quiet++; else { quiet = 0; last = n; }
            }

            var result = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(1))
                        result.AddRange(File.ReadAllLines(f).Where(l => l.Length > 0));
                    File.Delete(f);
                }
                catch (IOException) { }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
}

[ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFileDialog
{
    [PreserveSig] int Show(IntPtr hwndOwner);
    void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
    void SetFileTypeIndex(uint iFileType);
    void GetFileTypeIndex(out uint piFileType);
    void Advise(IntPtr pfde, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOptions(uint fos);
    void GetOptions(out uint pfos);
    void SetDefaultFolder(IShellItem psi);
    void SetFolder(IShellItem psi);
    void GetFolder(out IShellItem ppsi);
    void GetCurrentSelection(out IShellItem ppsi);
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
    void GetResult(out IShellItem ppsi);
}

[SupportedOSPlatform("windows")]
static class WinNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    public static void MessageBox(string text, bool error) =>
        MessageBoxW(IntPtr.Zero, text, Program.AppTitle,
            (error ? 0x10u : 0x40u) | 0x10000u | 0x40000u);

    public static string? PickFolder(string title, string? start)
    {
        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        var dlg = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"))!)!;
        try
        {
            dlg.GetOptions(out var opts);
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dlg.SetTitle(title);

            if (start != null)
            {
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out var folder);
                dlg.SetFolder(folder);
            }

            if (dlg.Show(IntPtr.Zero) != 0) return null;

            dlg.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return path;
        }
        finally
        {
            Marshal.FinalReleaseComObject(dlg);
        }
    }
}

static class Ui
{
    public static string? PickFolder(string title, string? start)
    {
        if (start != null && !Directory.Exists(start)) start = null;

        if (OperatingSystem.IsWindows()) return WinNative.PickFolder(title, start);
        if (OperatingSystem.IsMacOS()) return PickMac(title, start);
        return PickLinux(title, start);
    }

    public static void Error(string text) => Message(text, error: true);
    public static void Info(string text) => Message(text, error: false);

    static void Message(string text, bool error)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WinNative.MessageBox(text, error);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Exec("osascript", ["-e",
                    $"display dialog \"{AsEsc(text)}\" with title \"{Program.AppTitle}\" buttons {{\"OK\"}} default button 1 with icon {(error ? "caution" : "note")}"]);
            }
            else
            {
                try { Exec("zenity", [error ? "--error" : "--info", "--no-markup", "--title=" + Program.AppTitle, "--text=" + text]); }
                catch (Win32Exception) { Exec("kdialog", [error ? "--error" : "--msgbox", text, "--title", Program.AppTitle]); }
            }
        }
        catch
        {
            Console.Error.WriteLine(text);
        }
    }

    static string? PickMac(string title, string? start)
    {
        var loc = start != null ? $" default location POSIX file \"{AsEsc(start)}\"" : "";
        var (code, output) = Exec("osascript", ["-e", $"POSIX path of (choose folder with prompt \"{AsEsc(title)}\"{loc})"]);
        output = output.Trim();
        return code == 0 && output.Length > 0 ? output : null;
    }

    static string AsEsc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string? PickLinux(string title, string? start)
    {
        try
        {
            var args = new List<string> { "--file-selection", "--directory", "--title=" + title };
            if (start != null) args.Add("--filename=" + start.TrimEnd('/') + "/");
            var (code, output) = Exec("zenity", args);
            output = output.Trim();
            return code == 0 && output.Length > 0 ? output : null;
        }
        catch (Win32Exception) { }

        try
        {
            var args = new List<string> { "--getexistingdirectory", start ?? Paths.Home, "--title", title };
            var (code, output) = Exec("kdialog", args);
            output = output.Trim();
            return code == 0 && output.Length > 0 ? output : null;
        }
        catch (Win32Exception) { }

        throw new InvalidOperationException("zenity or kdialog not found.\nInstall: sudo apt install zenity");
    }

    static (int code, string output) Exec(string file, IEnumerable<string> args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}

static class Paths
{
    public static string Home
    {
        get
        {
            var h = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(h)) h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return h;
        }
    }

    public static string DataDir
    {
        get
        {
            if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library", "Application Support");
            var x = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrEmpty(x) ? Path.Combine(Home, ".local", "share") : x;
        }
    }
}

static class Settings
{
    static string File_ => Path.Combine(Paths.DataDir, "symlinkto", "last-dir.txt");

    public static string? LoadLastDir()
    {
        try { return File.Exists(File_) ? File.ReadAllText(File_).Trim() : null; }
        catch { return null; }
    }

    public static void SaveLastDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, dir);
        }
        catch { }
    }
}

static class Installer
{
    record Entry(string Id, string Title, string Args);

    static readonly Entry[] Entries =
    {
        new("SymlinkTo", Program.MenuTitle, ""),
        new("SymlinkToRemove", Program.RemoveTitle, "--remove "),
    };

    static readonly string[] WinClasses = { "*", "Directory" };

    static string Exe()
    {
        var p = Environment.ProcessPath;
        if (p == null || Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the published binary (dotnet publish), not via 'dotnet run'.");
        return p;
    }

    public static int Install()
    {
        var exe = Exe();
        if (OperatingSystem.IsWindows()) InstallWindows(exe);
        else if (OperatingSystem.IsMacOS()) InstallMac(exe);
        else InstallLinux(exe);
        return 0;
    }

    public static int Uninstall()
    {
        foreach (var e in Entries)
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var c in WinClasses)
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{c}\shell\{e.Id}", throwOnMissingSubKey: false);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var wf = Path.Combine(Home, "Library", "Services", e.Id + ".workflow");
                if (Directory.Exists(wf)) Directory.Delete(wf, true);
            }
            else
            {
                var f = LinuxScriptPath(e);
                if (File.Exists(f)) File.Delete(f);
            }
        }
        Ui.Info("Menu items removed.");
        return 0;
    }

    static string Home => Paths.Home;

    [SupportedOSPlatform("windows")]
    static void InstallWindows(string exe)
    {
        foreach (var e in Entries)
            foreach (var c in WinClasses)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{c}\shell\{e.Id}");
                key.SetValue("MUIVerb", e.Title);
                key.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = key.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" {e.Args}\"%1\"");
            }

        Ui.Info("Done. Added items \"" + Program.MenuTitle + "\" and \"" + Program.RemoveTitle + "\".\n\n" +
                "On Windows 11 they are under \"Show more options\" (or Shift+F10).\n" +
                "Do not move symlinkto.exe — the menu points to its current location.");
    }

    static string LinuxScriptPath(Entry e) => Path.Combine(Paths.DataDir, "nautilus", "scripts", e.Title);

    static void InstallLinux(string exe)
    {
        var quoted = "'" + exe.Replace("'", "'\\''") + "'";
        foreach (var e in Entries)
        {
            var path = LinuxScriptPath(e);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#!/bin/sh\nexec {quoted} {e.Args}\"$@\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                           UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        Ui.Info("Done. Restart Files with:  nautilus -q\n\n" +
                "The items will appear via: right click → Scripts → \"" + Program.MenuTitle + "\" / \"" + Program.RemoveTitle + "\".");
    }

    static void InstallMac(string exe)
    {
        foreach (var e in Entries) WriteWorkflow(e, exe);

        try { Process.Start("/System/Library/CoreServices/pbs", "-update")?.WaitForExit(3000); } catch { }

        Ui.Info("Done. Right-click a file in Finder → \"Quick Actions\" (or \"Services\") → \"" + Program.MenuTitle + "\" / \"" + Program.RemoveTitle + "\".\n\n" +
                "If the items are missing: System Settings → Keyboard → Keyboard Shortcuts → Services → Files and Folders → enable them.");
    }

    static void WriteWorkflow(Entry e, string exe)
    {
        var contents = Path.Combine(Home, "Library", "Services", e.Id + ".workflow", "Contents");
        Directory.CreateDirectory(contents);

        File.WriteAllText(Path.Combine(contents, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>NSServices</key>
              <array>
                <dict>
                  <key>NSMenuItem</key>
                  <dict><key>default</key><string>{SecurityElement.Escape(e.Title)}</string></dict>
                  <key>NSMessage</key><string>runWorkflowAsService</string>
                  <key>NSRequiredContext</key>
                  <dict><key>NSApplicationIdentifier</key><string>com.apple.finder</string></dict>
                  <key>NSSendFileTypes</key>
                  <array><string>public.item</string></array>
                </dict>
              </array>
            </dict>
            </plist>
            """);

        var command = SecurityElement.Escape($"\"{exe}\" {e.Args}\"$@\"");
        File.WriteAllText(Path.Combine(contents, "document.wflow"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>AMApplicationBuild</key><string>523</string>
              <key>AMApplicationVersion</key><string>2.10</string>
              <key>AMDocumentVersion</key><string>2</string>
              <key>actions</key>
              <array>
                <dict>
                  <key>action</key>
                  <dict>
                    <key>AMAccepts</key>
                    <dict>
                      <key>Container</key><string>List</string>
                      <key>Optional</key><true/>
                      <key>Types</key><array><string>com.apple.cocoa.string</string></array>
                    </dict>
                    <key>AMActionVersion</key><string>2.0.3</string>
                    <key>AMApplication</key><array><string>Automator</string></array>
                    <key>AMParameterProperties</key>
                    <dict>
                      <key>COMMAND_STRING</key><dict/>
                      <key>CheckedForUserDefaultShell</key><dict/>
                      <key>inputMethod</key><dict/>
                      <key>shell</key><dict/>
                      <key>source</key><dict/>
                    </dict>
                    <key>AMProvides</key>
                    <dict>
                      <key>Container</key><string>List</string>
                      <key>Types</key><array><string>com.apple.cocoa.string</string></array>
                    </dict>
                    <key>ActionBundlePath</key><string>/System/Library/Automator/Run Shell Script.action</string>
                    <key>ActionName</key><string>Run Shell Script</string>
                    <key>ActionParameters</key>
                    <dict>
                      <key>COMMAND_STRING</key><string>{command}</string>
                      <key>CheckedForUserDefaultShell</key><true/>
                      <key>inputMethod</key><integer>1</integer>
                      <key>shell</key><string>/bin/zsh</string>
                      <key>source</key><string></string>
                    </dict>
                    <key>BundleIdentifier</key><string>com.apple.RunShellScript</string>
                    <key>CFBundleVersion</key><string>2.0.3</string>
                    <key>CanShowSelectedItemsWhenRun</key><false/>
                    <key>CanShowWhenRun</key><true/>
                    <key>Category</key><array><string>AMCategoryUtilities</string></array>
                    <key>Class Name</key><string>RunShellScriptAction</string>
                    <key>InputUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                    <key>OutputUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                    <key>UUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                    <key>UnlocalizedApplications</key><array><string>Automator</string></array>
                    <key>arguments</key><dict/>
                    <key>isViewVisible</key><integer>1</integer>
                  </dict>
                  <key>isViewVisible</key><integer>1</integer>
                </dict>
              </array>
              <key>connectors</key><dict/>
              <key>workflowMetaData</key>
              <dict>
                <key>serviceInputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
                <key>serviceOutputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
                <key>serviceProcessesInput</key><integer>0</integer>
                <key>workflowTypeIdentifier</key><string>com.apple.Automator.servicesMenu</string>
              </dict>
            </dict>
            </plist>
            """);
    }
}