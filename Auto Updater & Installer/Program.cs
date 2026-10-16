// See https://aka.ms/new-console-template for more information
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Diagnostics;

Console.WriteLine("Hello, World!");

bool installNotUpdate = false;
bool doForAllUsers = false;
bool localToAU = false;
bool auToLocal = false;
// AU = All Users

//Copied and modified 14/10/2026
//Credit for original code (pre-modifications) to Anthony Simmon
//https://annaspannertechsolutions.uk/tinyurls/anthonysimmonelevateperms
static async Task<bool> getAdminPerms(string[] argsIfFail)
{
    bool isAdmin = false;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
    else
    {
        [DllImport("libc")]
        static extern uint geteuid();
        isAdmin = geteuid() == 0;
    }
    if (isAdmin) {return true;}
    else
    {
        var currentProcessPath = Environment.ProcessPath ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.ChangeExtension(typeof(Program).Assembly.Location, "exe")
        : Path.ChangeExtension(typeof(Program).Assembly.Location, null) 
        );
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var psi = new ProcessStartInfo {UseShellExecute=true,};
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.Verb = "runas";
            psi.FileName = currentProcessPath;
        }
        else if (isMac)
        {
            psi.FileName = "osascript";
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"do shell script \"{currentProcessPath} {string.Join(' ', argsIfFail)}\" with administrator privileges");
        }
        else
        {
            psi.FileName = "sudo";
            psi.ArgumentList.Add(currentProcessPath);
        }
        if (!isMac)
        {
            foreach (var arg in argsIfFail)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        try 
        {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");
        await process.WaitForExitAsync();
        }
        catch (InvalidOperationException) {return false;}
        }
    return false;
}

switch (args.Length)
{
    case 0:
        installNotUpdate = true;
        break;
    case 1:
        if (args[0][0] == '-' && args[0] == "-runAllUsers" )
        {
            doForAllUsers = true;
            installNotUpdate = true;
        }
        else
        {
            // if this arg (which is a path) is a all users path
            {
                while (true) 
                {
                    Console.Write("Your current isntillation is accessable by all users. Do you wish to still update (requires Admin Privilages) or move the isntallation to local user? (move/update) ->");
                    string? response = Console.ReadLine();
                    if (response!.ToLower().Equals("move"))
                    {
                        installNotUpdate = false;
                        auToLocal = true;
                        break;
                    }
                    else if (response!.ToLower().Equals("update"))
                    {
                        installNotUpdate = false;
                        doForAllUsers = await getAdminPerms(args);
                        break;
                    }
                }
                

            }
        }
        break;
    case 2:
    doForAllUsers = true;
        foreach (var arg in args)
        {
            if (!(arg[0] == '-'))
            {
                // if this arg (which is a path) is a local path, set localToAU = true;
            }
        }
        break;
}