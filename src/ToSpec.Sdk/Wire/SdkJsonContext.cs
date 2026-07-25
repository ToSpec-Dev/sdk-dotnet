using System.Text.Json.Serialization;

namespace ToSpec.Sdk.Wire;

/// <summary>
/// Source-generated JSON for the ingest wire contract. <b>snake_case</b> property names
/// and <b>omit-nulls</b> exactly mirror the platform ingest host
/// (<c>ToSpec.Ingest</c>): the batch the SDK serializes must deserialize on the server,
/// and the config the server serializes must deserialize here. This casing is also what
/// the <c>ToSpec-Dev/sdk-protocol</c> golden batches lock, so any community port that matches
/// these bytes is wire-compatible.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IngestBatch))]
[JsonSerializable(typeof(IngestEventEnvelope))]
[JsonSerializable(typeof(SdkConfigResponse))]
public sealed partial class SdkJsonContext : JsonSerializerContext;
