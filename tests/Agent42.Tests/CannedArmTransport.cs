using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

// Orleans.Runtime.Request is in scope via ImplicitUsings and collides with the Azure.Core one.
using Request = Azure.Core.Request;
using Response = Azure.Response;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="HttpPipelineTransport"/> that answers every ARM request with a canned
/// Cognitive Services "list deployments" payload, so <c>AzureModelCatalog</c>'s real query path can be
/// exercised offline — the ordering, the chat-capability filter, and (the one that actually matters for
/// multi-account) the qualification of each deployment under the RIGHT provider key.
///
/// <para>Without this the ARM body would be untested: the composite-catalog tests use a static
/// stand-in, which proves the composition but not the mapping. Records the URIs it was asked for, so a
/// test can assert that two Foundry providers really did query two different accounts.</para>
/// </summary>
internal sealed class CannedArmTransport(params string[] deploymentNames) : HttpPipelineTransport
{
    public List<string> RequestedUris { get; } = [];

    public override Request CreateRequest() => new CannedRequest();

    public override void Process(HttpMessage message) => ProcessAsync(message).GetAwaiter().GetResult();

    public override ValueTask ProcessAsync(HttpMessage message)
    {
        var uri = message.Request.Uri.ToString();
        RequestedUris.Add(uri);

        // The ARM deserializer requires each item to carry its resource `id` — without one it throws
        // ArgumentNullException rather than returning an empty page. Derive it from the very path that
        // was requested, so the canned response is a faithful echo of the account being listed.
        var basePath = new Uri(uri).AbsolutePath.TrimEnd('/');

        var items = string.Join(",", deploymentNames.Select(name => $$"""
            {
              "id": "{{basePath}}/{{name}}",
              "name": "{{name}}",
              "type": "Microsoft.CognitiveServices/accounts/deployments",
              "properties": {
                "provisioningState": "Succeeded",
                "capabilities": { "chatCompletion": "true" },
                "model": { "name": "{{name}}", "version": "2024-08-06" }
              }
            }
            """));

        message.Response = new CannedResponse($$"""{ "value": [{{items}}] }""");
        return ValueTask.CompletedTask;
    }

    private sealed class CannedRequest : Request
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public override string ClientRequestId { get; set; } = Guid.Empty.ToString();

        protected override void AddHeader(string name, string value) => _headers[name] = value;

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
            => _headers.Select(h => new HttpHeader(h.Key, h.Value));

        protected override bool RemoveHeader(string name) => _headers.Remove(name);

        protected override bool TryGetHeader(string name, out string? value) => _headers.TryGetValue(name, out value);

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string>? values)
        {
            values = _headers.TryGetValue(name, out var value) ? [value] : null;
            return values is not null;
        }

        public override void Dispose()
        {
        }
    }

    private sealed class CannedResponse(string json) : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream ContentStream { get; set; } = new MemoryStream(Encoding.UTF8.GetBytes(json));

        public override string ClientRequestId { get; set; } = Guid.Empty.ToString();

        protected override bool ContainsHeader(string name) => name == "Content-Type";

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
            => [new HttpHeader("Content-Type", "application/json")];

        protected override bool TryGetHeader(string name, out string? value)
        {
            value = name == "Content-Type" ? "application/json" : null;
            return value is not null;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string>? values)
        {
            values = TryGetHeader(name, out var value) ? [value!] : null;
            return values is not null;
        }

        public override void Dispose() => ContentStream?.Dispose();
    }
}
