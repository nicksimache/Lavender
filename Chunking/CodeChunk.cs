using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Chunking
{
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
        public int StartLine { get; set; } = 0;
        public int EndLine { get; set; } = 0;
        public string Code { get; set; } = "";
        public string EmbeddingText { get; set; } = "";
    }

    /// <summary>
    /// Code chunk data structure that gets sent to python api
    /// </summary>
    public class PythonCodeChunk
    {
        public string id { get; set; } = "";
        public string file_path { get; set; } = "";
        public string chunk_type { get; set; } = "";
        public string @namespace { get; set; } = "";
        public string class_name { get; set; } = "";
        public string member_name { get; set; } = "";
        public string signature { get; set; } = "";
        public int start_line { get; set; } = 0;
        public int end_line { get; set; } = 0;
        public string code { get; set; } = "";
        public string embedding_text { get; set; } = "";

        public static PythonCodeChunk ToPythonChunk(Chunking.CodeChunk chunk)
        {
            return new PythonCodeChunk
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
    /// New chunk class representing the data that gets returned from vector search in python service
    /// </summary>
    public class VectorSearchCodeChunk
    {
        public string file_path { get; set; } = "";
        public string chunk_type { get; set; } = "";
        public string @namespace { get; set; } = "";
        public string class_name { get; set; } = "";
        public string member_name { get; set; } = "";
        public string signature { get; set; } = "";
        public int start_line { get; set; } = 0;
        public int end_line { get; set; } = 0;
        public string code { get; set; } = "";
        public double distance { get; set; } = 0.0;
    }

    /// <summary>
    /// Return type of the search api
    /// </summary>
    public class VectorSearchCodeChunkObject
    {
        public List<VectorSearchCodeChunk> list = new List<VectorSearchCodeChunk>();
    }
}
