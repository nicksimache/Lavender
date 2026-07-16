using System.Text.Json.Serialization;

namespace Lavender.Core.DataTypes
{

    /// <summary>
    /// Code chunk data structure for semantic search
    /// </summary>
    public class CodeChunk
    {
        public enum E_ChunkType
        {
            WholeFile,
            FileSummary,
            ClassFields,
            Method,
            Property,
            Constructor,
            Interface,
            Struct,
            Enum,
            Record,
            None
        }

        public string Id { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public E_ChunkType ChunkType { get; set; } = E_ChunkType.None;
        public string Namespace { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string Signature { get; set; } = "";
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Code { get; set; } = "";
        public string EmbeddingText { get; set; } = "";
    }

    /// <summary>
    /// CodeChunk object that gets sent to python api for semantic search
    /// </summary>
    public class CodeChunk_ToPython
    {
        public string id { get; set; } = "";
        public string file_path { get; set; } = "";
        public string chunk_type { get; set; } = "";
        public string @namespace { get; set; } = "";
        public string class_name { get; set; } = "";
        public string member_name { get; set; } = "";
        public string signature { get; set; } = "";
        public int start_line { get; set; }
        public int end_line { get; set; }
        public string code { get; set; } = "";
        public string embedding_text { get; set; } = "";

        /// <summary>
        /// Takes in a code chunk and returns it in the python object form
        /// </summary>
        /// <param name="chunk"></param>
        /// <returns></returns>
        public static CodeChunk_ToPython ToPythonChunk(CodeChunk chunk)
        {
            return new CodeChunk_ToPython
            {
                id = chunk.Id,
                file_path = chunk.FilePath,
                chunk_type = chunk.ChunkType.ToString(),
                @namespace = chunk.Namespace,
                class_name = chunk.ClassName,
                member_name = chunk.MemberName,
                signature = chunk.Signature,
                start_line = chunk.StartLine,
                end_line = chunk.EndLine,
                code = chunk.Code,
                embedding_text = chunk.EmbeddingText
            };
        }
    }

    /// <summary>
    /// Code chunk object with score that is the result of semantic search
    /// Serialized in JSON to enable transfer between backend
    /// </summary>
    public class VectorSearchCodeChunk
    {
        [JsonPropertyName("file_path")]
        public string FilePath { get; set; } = "";

        [JsonPropertyName("chunk_type")]
        public string ChunkType { get; set; } = "";

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("class_name")]
        public string ClassName { get; set; } = "";

        [JsonPropertyName("member_name")]
        public string MemberName { get; set; } = "";

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = "";

        [JsonPropertyName("start_line")]
        public int StartLine { get; set; }

        [JsonPropertyName("end_line")]
        public int EndLine { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("distance")]
        public double Distance { get; set; }
    }

    /// <summary>
    /// Code chunk object that is returned by call to the semantic search api
    /// Serialized with JSON to enable transfer between backend
    /// </summary>
    public class VectorSearchCodeChunk_ObjectRecv
    {
        [JsonPropertyName("results")]
        public List<VectorSearchCodeChunk> Results { get; set; } = new();
    }
}
