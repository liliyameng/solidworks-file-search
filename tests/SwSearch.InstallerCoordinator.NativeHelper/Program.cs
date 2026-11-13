using System.Text;

if (args.Length < 3 ||
    !(args[0] is "--native-gate-helper" or "--native-gate-child" or
        "--app-settings-noncooperative" or "--app-registry-noncooperative") ||
    !Guid.TryParseExact(args[1], "N", out _))
{
    return 64;
}

var nonce = args[1];
var readyPath = Path.GetFullPath(args[2]);
var publishingPath = readyPath + ".publishing-" + nonce;
if (args[0] == "--app-settings-noncooperative")
{
    if (args.Length != 4) return 65;
    File.WriteAllText(Path.GetFullPath(args[3]), nonce, new UTF8Encoding(false));
}
try
{
    File.WriteAllText(
        publishingPath,
        nonce + "|" + Environment.ProcessId.ToString(),
        new UTF8Encoding(false));
    File.Move(publishingPath, readyPath);
}
finally
{
    if (File.Exists(publishingPath))
    {
        File.Delete(publishingPath);
    }
}

Thread.Sleep(Timeout.Infinite);
if (args[0] == "--app-registry-noncooperative" && args.Length == 4)
{
    File.WriteAllText(Path.GetFullPath(args[3]), nonce, new UTF8Encoding(false));
}
return 0;
