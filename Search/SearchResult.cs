using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Search
{
    internal class SearchResult
    {
        public string FilePath { get; set; } = "";
        public int Score { get; set; }
    }
}
