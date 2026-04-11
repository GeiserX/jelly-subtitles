using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperSubs.Configuration
{
    /// <summary>
    /// JSON converter that treats null or out-of-range values as <see cref="SubtitleMode.Full"/>.
    /// Prevents 500 errors when the config page sends SubtitleMode: null
    /// (parseInt on an uninitialized select returns NaN, which JSON.stringify emits as null).
    /// </summary>
    public class SubtitleModeConverter : JsonConverter<SubtitleMode>
    {
        /// <summary>
        /// Allow the converter to handle JSON null tokens for this value type.
        /// </summary>
        public override bool HandleNull => true;

        /// <inheritdoc />
        public override SubtitleMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return SubtitleMode.Full;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
            {
                return Enum.IsDefined((SubtitleMode)value) ? (SubtitleMode)value : SubtitleMode.Full;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString();
                if (Enum.TryParse<SubtitleMode>(str, true, out var parsed))
                {
                    return parsed;
                }
            }

            return SubtitleMode.Full;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, SubtitleMode value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((int)value);
        }
    }
}
