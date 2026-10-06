using System.Text;
using BlazorAzureInventory.Services.AI;
using Microsoft.Extensions.AI;

namespace BlazorAzureInventory.Tests;

public class GoogleAIChatClientTests
{
    [Fact]
    public async Task Streaming_function_call_is_invoked_and_signature_is_sent_back()
    {
        var handler = new FakeAIHandler(
            FakeAIHandler.Sse(
                """{"candidates":[{"content":{"role":"model","parts":[{"text":"Checking."}]}}],"responseId":"r1"}""",
                """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"fc_1","name":"GetApp","args":{"name":"shop"}},"thoughtSignature":"c2ln"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":4},"responseId":"r1"}"""),
            FakeAIHandler.Sse(
                """{"candidates":[{"content":{"role":"model","parts":[{"text":"shop is healthy"}]},"finishReason":"STOP"}],"responseId":"r2"}"""));

        string? calledWith = null;
        var tool = AIFunctionFactory.Create((string name) => { calledWith = name; return new { status = "healthy" }; }, "GetApp", "Gets an app.");
        using var client = new GoogleAIChatClient(new HttpClient(handler), "key", "gemini-3.8-flash", 256)
            .AsBuilder().UseFunctionInvocation().Build();

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "How is shop?")],
            new ChatOptions { Tools = [tool] }))
        {
            text.Append(update.Text);
        }

        Assert.Equal("shop", calledWith);
        Assert.Equal("Checking.shop is healthy", text.ToString());
        Assert.Equal(2, handler.Bodies.Count);
        Assert.EndsWith("models/gemini-3.8-flash:streamGenerateContent?alt=sse", handler.Uris[0].ToString());

        var first = handler.Bodies[0];
        Assert.Equal("sys", first["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        var declaration = first["tools"]![0]!["functionDeclarations"]![0]!;
        Assert.Equal("GetApp", declaration["name"]!.GetValue<string>());
        Assert.Equal("object", declaration["parametersJsonSchema"]!["type"]!.GetValue<string>());
        Assert.NotNull(declaration["parametersJsonSchema"]!["properties"]!["name"]);

        var contents = handler.Bodies[1]["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => c!["role"]!.GetValue<string>()));
        var callPart = contents[1]!["parts"]!.AsArray().Single(p => p!["functionCall"] is not null)!;
        Assert.Equal("c2ln", callPart["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("fc_1", callPart["functionCall"]!["id"]!.GetValue<string>());
        Assert.Equal("shop", callPart["functionCall"]!["args"]!["name"]!.GetValue<string>());

        var response = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("GetApp", response["name"]!.GetValue<string>());
        Assert.Equal("fc_1", response["id"]!.GetValue<string>());
        Assert.Equal("healthy", response["response"]!["result"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Calls_without_ids_get_local_ids_that_are_not_sent_back()
    {
        var handler = new FakeAIHandler(
            FakeAIHandler.Json("""{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"Stats","args":{}}},{"functionCall":{"name":"Stats","args":{"top":2}}}]},"finishReason":"STOP"}]}"""),
            FakeAIHandler.Json("""{"candidates":[{"content":{"role":"model","parts":[{"text":"done"}]},"finishReason":"STOP"}]}"""));

        var calls = 0;
        var tool = AIFunctionFactory.Create((int top = 0) => { calls++; return "ok"; }, "Stats", "Stats.");
        using var client = new GoogleAIChatClient(new HttpClient(handler), "key", "gemini-2.5-pro", 256)
            .AsBuilder().UseFunctionInvocation().Build();

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "stats")], new ChatOptions { Tools = [tool] });

        Assert.Equal(2, calls);
        Assert.Equal("done", response.Text);
        var contents = handler.Bodies[1]["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => c!["role"]!.GetValue<string>()));
        Assert.All(contents[1]!["parts"]!.AsArray(), p => Assert.Null(p!["functionCall"]!["id"]));
        var results = contents[2]!["parts"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.All(results, p =>
        {
            Assert.Equal("Stats", p!["functionResponse"]!["name"]!.GetValue<string>());
            Assert.Null(p["functionResponse"]!["id"]);
            Assert.Equal("ok", p["functionResponse"]!["response"]!["result"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Non_streaming_response_maps_text_finish_reason_and_usage()
    {
        var handler = new FakeAIHandler(FakeAIHandler.Json("""
            {"candidates":[{"content":{"role":"model","parts":[{"text":"thinking...","thought":true},{"text":"OK"}]},"finishReason":"MAX_TOKENS"}],
             "usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1,"totalTokenCount":6},"modelVersion":"gemini-2.5-pro"}
            """));
        using var client = new GoogleAIChatClient(new HttpClient(handler), "key", "gemini-2.5-pro", 256);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("OK", response.Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.Equal(6, response.Usage!.TotalTokenCount);
        Assert.Null(handler.Bodies[0]["tools"]);
        Assert.EndsWith("models/gemini-2.5-pro:generateContent", handler.Uris[0].ToString());
    }
}
