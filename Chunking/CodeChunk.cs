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
}
