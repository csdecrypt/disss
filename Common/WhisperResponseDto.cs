using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    public class WhisperResponseDto
    {
        public string text { get; set; }
        public List<Chunk> chunks { get; set; }
    }
    public class Chunk
    {
        public string text { get; set; }
        public List<double> timestamp { get; set; }
    }
}
