using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the shape of the Claude <c>user</c> frame with attachments. The stream-json
/// input forwards content blocks to the API, so an image must arrive as a base64
/// <c>image</c> block and a PDF as a <c>document</c> block - a stray field or wrong
/// media type surfaces only as an inscrutable API error mid-turn.
/// </summary>
public sealed class ClaudeAttachmentFrameTests
{
    private static JsonElement Frame(string text, params ClaudeAgentSession.AttachmentPayload[] payloads) =>
        JsonDocument.Parse(ClaudeAgentSession.SerializeFrame(
            ClaudeAgentSession.BuildUserMessageFrame(text, payloads))).RootElement;

    private static ClaudeAgentSession.AttachmentPayload Payload(string path) => new(
        new TurnAttachment
        {
            Kind = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? TurnAttachmentKind.Pdf
                : TurnAttachmentKind.Image,
            Name = Path.GetFileName(path),
            Path = path,
        },
        Convert.ToBase64String("fake-bytes"u8.ToArray()));

    [Fact]
    public void Text_and_image_arrive_as_separate_content_blocks()
    {
        var root = Frame("what is in this shot?", Payload(@"C:\pics\shot.png"));

        Assert.Equal("user", root.GetProperty("type").GetString());

        var message = root.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());

        var content = message.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());

        var text = content[0];
        Assert.Equal("text", text.GetProperty("type").GetString());
        Assert.Equal("what is in this shot?", text.GetProperty("text").GetString());

        var image = content[1];
        Assert.Equal("image", image.GetProperty("type").GetString());

        var source = image.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.False(string.IsNullOrEmpty(source.GetProperty("data").GetString()));
    }

    [Fact]
    public void Pdf_travels_as_a_document_block()
    {
        var root = Frame("summarise", Payload(@"C:\docs\spec.pdf"));

        var document = root.GetProperty("message").GetProperty("content")[1];

        Assert.Equal("document", document.GetProperty("type").GetString());
        Assert.Equal(
            "application/pdf",
            document.GetProperty("source").GetProperty("media_type").GetString());
    }

    [Fact]
    public void Jpeg_extension_maps_to_the_jpeg_media_type()
    {
        var root = Frame("read this", Payload(@"C:\pics\photo.jpeg"));

        Assert.Equal(
            "image/jpeg",
            root.GetProperty("message").GetProperty("content")[1].GetProperty("source").GetProperty("media_type").GetString());
    }

    [Fact]
    public void A_text_only_turn_stays_a_single_text_block()
    {
        var root = Frame("just words");

        var content = root.GetProperty("message").GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
    }

    [Fact]
    public void An_attachment_only_turn_sends_no_empty_text_block()
    {
        var root = Frame("", Payload(@"C:\pics\shot.png"));

        var content = root.GetProperty("message").GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
    }
}

/// <summary>
/// Pins the <c>set_permission_mode</c> control request: the frame that switches a
/// running Claude session into plan mode (and back) without a restart. The CLI
/// validates control requests strictly, so the shape is load-bearing.
/// </summary>
public sealed class ClaudeSetPermissionModeFrameTests
{
    [Fact]
    public void The_mode_rides_the_request_object()
    {
        var json = ClaudeAgentSession.SerializeFrame(
            ClaudeAgentSession.BuildControlRequestFrame(
                "codale_7", "set_permission_mode", request => request["mode"] = "plan"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("control_request", root.GetProperty("type").GetString());
        Assert.Equal("codale_7", root.GetProperty("request_id").GetString());

        var request = root.GetProperty("request");
        Assert.Equal("set_permission_mode", request.GetProperty("subtype").GetString());
        Assert.Equal("plan", request.GetProperty("mode").GetString());
    }
}
