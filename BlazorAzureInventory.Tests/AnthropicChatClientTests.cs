using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlazorAzureInventory.Models.AI;
using BlazorAzureInventory.Services.AI;
using Microsoft.Extensions.AI;

namespace BlazorAzureInventory.Tests;

public class AnthropicChatClientTests
{
    private static readonly Uri MessagesUri = new("https://api.anthropic.com/v1/messages");

    [Theory]
    [InlineData(AIServiceType.Anthropic, FoundryApiProtocol.Auto, "claude-opus-4-8", true)]
    [InlineData(AIServiceType.AzureAIFoundry, FoundryApiProtocol.AnthropicMessages, "claude-opus-4-8", true)]
    [InlineData(AIServiceType.GoogleAI, FoundryApiProtocol.Auto, "gemini-3.8-flash", true)]
    [InlineData(AIServiceType.OpenAI, FoundryApiProtocol.Auto, "gpt-5.6", true)]
    [InlineData(AIServiceType.AzureOpenAI, FoundryApiProtocol.Auto, "watts-gpt4o", true)]
    [InlineData(AIServiceType.AzureOpenAI, FoundryApiProtocol.Auto, "contoso_instructor-chat", true)]
    [InlineData(AIServiceType.OpenAI, FoundryApiProtocol.Auto, "gpt-4o-mini-tts", false)]
    [InlineData(AIServiceType.OpenAI, FoundryApiProtocol.Auto, "text-embedding-3-small", false)]
    [InlineData(AIServiceType.OpenAI, FoundryApiProtocol.Auto, "gpt-3.5-turbo-instruct", false)]
    [InlineData(AIServiceType.OpenAI, FoundryApiProtocol.Auto, "o1-mini-2024-09-12", false)]
    [InlineData(AIServiceType.OpenAI, FoundryApiProtocol.Auto, "gpt-4o-realtime-preview", false)]
    public void Capabilities_reflect_model_tool_support(AIServiceType provider, FoundryApiProtocol protocol, string model, bool expected) =>
        Assert.Equal(expected, AICapabilities.SupportsToolCalling(provider, model, protocol));

    [Fact]
    public async Task Streaming_tool_call_is_invoked_and_result_is_sent_back()
    {
        var handler = new FakeAIHandler(
            Sse(
                """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":10}}}""",
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Checking."}}""",
                """{"type":"content_block_stop","index":0}""",
                """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"GetApp","input":{}}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"na"}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"me\":\"shop\"}"}}""",
                """{"type":"content_block_stop","index":1}""",
                """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""",
                """{"type":"message_stop"}"""),
            Sse(
                """{"type":"message_start","message":{"id":"msg_2"}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"shop is healthy"}}""",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}"""));

        string? calledWith = null;
        var tool = AIFunctionFactory.Create((string name) => { calledWith = name; return "healthy"; }, "GetApp", "Gets an app.");
        using var client = new AnthropicChatClient(new HttpClient(handler), MessagesUri, "key", "claude-opus-4-8", 256)
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

        var first = handler.Bodies[0];
        Assert.Equal("sys", first["system"]!.GetValue<string>());
        var schema = first["tools"]![0]!;
        Assert.Equal("GetApp", schema["name"]!.GetValue<string>());
        Assert.Equal("object", schema["input_schema"]!["type"]!.GetValue<string>());
        Assert.NotNull(schema["input_schema"]!["properties"]!["name"]);

        var turns = handler.Bodies[1]["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], turns.Select(t => t!["role"]!.GetValue<string>()));
        var toolUse = turns[1]!["content"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "tool_use")!;
        Assert.Equal("toolu_1", toolUse["id"]!.GetValue<string>());
        Assert.Equal("shop", toolUse["input"]!["name"]!.GetValue<string>());
        var toolResult = turns[2]!["content"]![0]!;
        Assert.Equal("tool_result", toolResult["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.Contains("healthy", toolResult["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Non_streaming_response_parses_tool_use_blocks()
    {
        var handler = new FakeAIHandler(Json("""
            {"id":"msg_1","model":"claude-opus-4-8","stop_reason":"tool_use",
             "content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_9","name":"Stats","input":{"top":3}}],
             "usage":{"input_tokens":7,"output_tokens":2}}
            """));
        using var client = new AnthropicChatClient(new HttpClient(handler), MessagesUri, "key", "claude-opus-4-8", 256);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal("Let me look.", response.Text);
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("toolu_9", call.CallId);
        Assert.Equal("Stats", call.Name);
        Assert.Equal("3", call.Arguments!["top"]!.ToString());
        Assert.Equal(7, response.Usage!.InputTokenCount);
        Assert.Null(handler.Bodies[0]["tools"]);
    }

    [Fact]
    public async Task Temperature_is_dropped_and_remembered_when_model_rejects_it()
    {
        const string model = "claude-test-no-temperature";
        var handler = new FakeAIHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"`temperature` is deprecated for this model."}}""")
            },
            Json("""{"id":"msg_1","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}"""),
            Json("""{"id":"msg_2","stop_reason":"end_turn","content":[{"type":"text","text":"again"}]}"""));
        using var client = new AnthropicChatClient(new HttpClient(handler), MessagesUri, "key", model, 256);
        var options = new ChatOptions { Temperature = 0.2f };

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        Assert.Equal("ok", first.Text);
        Assert.Equal("again", second.Text);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.NotNull(handler.Bodies[0]["temperature"]);
        Assert.Null(handler.Bodies[1]["temperature"]);
        Assert.Null(handler.Bodies[2]["temperature"]);
    }

    [Fact]
    public async Task Other_bad_requests_are_not_retried()
    {
        var handler = new FakeAIHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: too large"}}""")
        });
        using var client = new AnthropicChatClient(new HttpClient(handler), MessagesUri, "key", "claude-test-other-400", 256);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Temperature = 0.2f }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Single(handler.Bodies);
    }

    private static HttpResponseMessage Sse(params string[] events) => FakeAIHandler.Sse(events);

    private static HttpResponseMessage Json(string json) => FakeAIHandler.Json(json);
}

internal sealed class FakeAIHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int _next;
    public List<JsonNode> Bodies { get; } = [];
    public List<Uri> Uris { get; } = [];

    public static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events.Select(e => $"event: x\ndata: {e}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uris.Add(request.RequestUri!);
        Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
        return responses[_next++];
    }
}
