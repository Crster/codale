using Codale.Mcp.Phone;

namespace Codale.Mcp.Tests;

public sealed class PhoneToolsTests
{
    [Fact]
    public void InputText_escapes_spaces_and_shell_characters()
    {
        Assert.Equal("hello%sworld", InputText.Escape("hello world"));
        Assert.Equal(@"a\&b\;c", InputText.Escape("a&b;c"));
        Assert.Equal(@"100\%", InputText.Escape("100%"));
    }

    [Fact]
    public void InputText_refuses_newlines()
    {
        Assert.Throws<McpToolException>(() => InputText.Escape("a\nb"));
    }

    [Fact]
    public void UiTree_lists_labelled_and_clickable_elements_with_centers()
    {
        const string xml = """
            UI hierarchy dumped to: /dev/tty
            <?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
            <hierarchy rotation="0">
              <node class="android.widget.FrameLayout" text="" resource-id="" content-desc="" clickable="false" bounds="[0,0][1080,2400]">
                <node class="android.widget.Button" text="Sign in" resource-id="com.x:id/login" content-desc="" clickable="true" bounds="[100,200][300,400]" />
                <node class="android.widget.ImageView" text="" resource-id="" content-desc="Logo" clickable="false" bounds="[0,0][100,100]" />
              </node>
            </hierarchy>
            """;

        var lines = UiTree.Parse(xml).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Contains("Button \"Sign in\"", lines[0]);
        Assert.Contains("id=com.x:id/login", lines[0]);
        Assert.Contains("center=(200,300)", lines[0]);
        Assert.EndsWith("clickable", lines[0]);
        Assert.Contains("desc=\"Logo\"", lines[1]);
    }

    [Fact]
    public void UiTree_rejects_output_without_a_hierarchy()
    {
        Assert.Throws<McpToolException>(() => UiTree.Parse("ERROR: null root node returned by UiTestAutomationBridge."));
    }

    [Fact]
    public void ParseDevices_reads_serial_state_and_detail()
    {
        var devices = Adb.ParseDevices("List of devices attached\nemulator-5554\tdevice product:sdk model:Pixel_8\nR58M\tunauthorized\n\n");

        Assert.Equal(2, devices.Count);
        Assert.Equal(("emulator-5554", "device", "product:sdk model:Pixel_8"), devices[0]);
        Assert.Equal("unauthorized", devices[1].State);
    }

    [Fact]
    public void ForDevice_prefixes_the_serial()
    {
        Assert.Equal(["-s", "abc", "install", "-r"], Adb.ForDevice("abc", "install", "-r"));
    }

    [Fact]
    public void ConfineToProject_allows_inside_and_refuses_outside()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");

        Assert.Equal(Path.Combine(root, "app", "a.apk"), Adb.ConfineToProject(Path.Combine("app", "a.apk"), root));
        Assert.Throws<McpToolException>(() => Adb.ConfineToProject(Path.Combine("..", "secret.txt"), root));
        Assert.Throws<McpToolException>(() => Adb.ConfineToProject(Path.Combine(Path.GetTempPath(), "other", "x"), root));
        Assert.Throws<McpToolException>(() => Adb.ConfineToProject("a.apk", ""));
    }

    [Fact]
    public async Task Server_lists_the_phone_tools()
    {
        var server = new McpServer("codale-phone", "1.0.0", PhoneTools.Create(new Adb()));

        var reply = (await server.HandleLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))!;
        var names = reply["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();

        Assert.Contains("phone_screenshot", names);
        Assert.Contains("phone_install", names);
        Assert.All(names, n => Assert.StartsWith("phone_", n));
    }
}
