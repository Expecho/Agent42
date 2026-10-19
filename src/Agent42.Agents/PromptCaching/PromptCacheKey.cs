using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// The per-conversation prompt-cache key (ADR 0104). Azure OpenAI routes requests that share a
/// <c>prompt_cache_key</c> to the same cache, so one conversation's turns keep hitting the prefix it built.
///
/// <para>The grain key carries the user id, so it is <b>hashed</b> and never sent raw. The key is still an opaque,
/// stable identifier that makes one conversation's requests linkable at the provider: an ADR 0013 egress
/// property, recorded in ADR 0104.</para>
///
/// <para>It travels in <c>ChatOptions.AdditionalProperties</c> under <see cref="PropertyName"/>, because the chat
/// client is shared across conversations; the Backend's <c>PromptCacheKeyChatClient</c> removes it and, on Azure
/// OpenAI only, maps it onto the SDK's request options.</para>
/// </summary>
public static class PromptCacheKey
{
    /// <summary>The <c>ChatOptions.AdditionalProperties</c> key the conversation's cache key travels under.</summary>
    public const string PropertyName = "agent42.prompt_cache_key";

    /// <summary><c>"a42-"</c> + the first 32 hex characters of SHA-256 over the grain key.</summary>
    public static string For(string grainKey)
        => "a42-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grainKey)))[..32];
}
