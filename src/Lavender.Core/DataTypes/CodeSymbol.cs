using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Core.DataTypes
{
    /// <summary>
    /// 
    /// </summary>
    public class CodeSymbol
    {
        public enum E_SymbolType
        {
            None,
            Namespace,
            Class,
            Struct,
            Interface,
            Record,
            Enum,
            Delegate,
            Method,
            Constructor,
            Destructor,
            Operator,
            ConversionOperator,
            Property,
            Indexer,
            Field,
            EnumMember,
            Event
        }

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FullyQualifiedName { get; set; } = "";
        public E_SymbolType SymbolType { get; set; } = E_SymbolType.None;
        public string Namespace { get; set; } = "";
        public string ContainingType { get; set; } = "";
        public string Signature { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int StartLine { get; set; } = 0;
    }
}
