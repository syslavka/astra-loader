using NLCleaner.Services;

void Reject(string path)
{
    try { DesktopPreferences.StartupCommand(path); }
    catch (ArgumentException) { return; }
    throw new Exception("Invalid startup path was accepted: " + path);
}
var command = DesktopPreferences.StartupCommand(@"C:\Program Files\NL-Cleaner\NL-Cleaner.exe");
if (command != "\"C:\\Program Files\\NL-Cleaner\\NL-Cleaner.exe\" --manual") throw new Exception("Incorrect path quoting or startup operation.");
Console.WriteLine("PASS Paths with spaces are quoted; startup opens only the menu.");
Reject(@"relative\NL-Cleaner.exe");
Console.WriteLine("PASS Relative startup executable paths are rejected.");
Reject("C:\\NL-Cleaner.exe\" --run");
Reject("C:\\NL-Cleaner\n.exe");
Console.WriteLine("PASS Quotes and line-break injection are rejected.");
Reject(@"C:\NL-Cleaner.dll");
Console.WriteLine("PASS Non-executable startup targets are rejected.");
Reject("C:\\" + new string('x', 260) + ".exe");
Console.WriteLine("PASS Overlong Windows Run commands are rejected.");
Console.WriteLine("5 startup checks passed. No registry or startup settings were changed.");
