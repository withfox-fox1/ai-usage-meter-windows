using System.Xml.Linq;
using AIUsageMeter.Core;

namespace AIUsageMeter.Tests;

public class LogonTaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Build(string path = @"C:\Apps\AIUsageMeter.exe", string directory = @"C:\Apps", string user = @"PC\user") =>
        XDocument.Parse(LogonTaskXml.Build(path, directory, user));

    private static string Value(XDocument doc, string name) => doc.Descendants(Ns + name).Single().Value;

    [Fact]
    public void TargetsTheGivenExecutableAndUser()
    {
        var doc = Build();

        Assert.Equal(@"C:\Apps\AIUsageMeter.exe", Value(doc, "Command"));
        Assert.Equal(@"C:\Apps", Value(doc, "WorkingDirectory"));
        Assert.Equal(@"PC\user", doc.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal(@"PC\user", doc.Descendants(Ns + "Principal").Single().Element(Ns + "UserId")!.Value);
    }

    [Fact]
    public void AllowsAResidentAppToKeepRunning()
    {
        var doc = Build();

        Assert.Equal("PT0S", Value(doc, "ExecutionTimeLimit"));
        Assert.Equal("false", Value(doc, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(doc, "StopIfGoingOnBatteries"));
        Assert.Equal("IgnoreNew", Value(doc, "MultipleInstancesPolicy"));
        Assert.Equal("5", Value(doc, "Priority"));
    }

    [Fact]
    public void RunsWithoutElevationInTheUsersSession()
    {
        var doc = Build();

        Assert.Equal("InteractiveToken", Value(doc, "LogonType"));
        Assert.Equal("LeastPrivilege", Value(doc, "RunLevel"));
    }

    [Fact]
    public void EscapesXmlSpecialCharacters()
    {
        var doc = Build(path: @"C:\A&B's <x>\AIUsageMeter.exe", directory: @"C:\A&B's <x>", user: @"PC\a&b");

        Assert.Equal(@"C:\A&B's <x>\AIUsageMeter.exe", Value(doc, "Command"));
        Assert.Equal(@"C:\A&B's <x>", Value(doc, "WorkingDirectory"));
        Assert.Equal(@"PC\a&b", doc.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
    }
}
