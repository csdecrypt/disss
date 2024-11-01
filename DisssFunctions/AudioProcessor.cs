using NAudio.Wave;

namespace DisssFunctions
{
    public static class AudioProcessor
    {
        public static (byte[] trimmedAudio, double secondsShavedOff) TrimSilenceFromAudio(byte[] audioBytes, float silenceThreshold = -20.0f, int chunkSizeMs = 10, int paddingMs = 200)
        {
            using (var inputStream = new MemoryStream(audioBytes))
            using (var reader = new WaveFileReader(inputStream))
            {
                int bytesPerMillisecond = reader.WaveFormat.AverageBytesPerSecond / 1000;
                double originalDuration = (double)reader.Length / reader.WaveFormat.AverageBytesPerSecond;

                int startPosition = GetPositionOfFirstSound(reader, silenceThreshold, chunkSizeMs, bytesPerMillisecond);
                int endPosition = GetPositionOfLastSound(reader, silenceThreshold, chunkSizeMs, bytesPerMillisecond);

                // Add padding to start and end positions
                startPosition = Math.Max(0, startPosition - paddingMs); // Ensure it doesn’t go below 0
                endPosition = Math.Min((int)(reader.Length / bytesPerMillisecond), endPosition + paddingMs);

                if (startPosition >= endPosition)
                {
                    return (null, 0); // The entire audio is either silence or incorrectly detected as such
                }

                int trimStartBytes = startPosition * bytesPerMillisecond;
                int trimEndBytes = endPosition * bytesPerMillisecond;

                var trimmedBytes = new byte[trimEndBytes - trimStartBytes];
                reader.Position = trimStartBytes;
                reader.Read(trimmedBytes, 0, trimmedBytes.Length);

                // Create a new WAV file with default format (e.g., 16 kHz, mono, 16-bit PCM)
                using (var outputStream = new MemoryStream())
                {
                    // Define the new WAV format (16 kHz, mono, 16-bit PCM)
                    var newFormat = new WaveFormat(16000, 16, 1);

                    using (var writer = new WaveFileWriter(outputStream, newFormat))
                    {
                        writer.Write(trimmedBytes, 0, trimmedBytes.Length);
                    }
                    double trimmedDuration = (double)trimmedBytes.Length / (newFormat.AverageBytesPerSecond);
                    double secondsShavedOff = originalDuration - trimmedDuration;

                    return (outputStream.ToArray(), secondsShavedOff); 
                }
            }
        }

        private static int GetPositionOfFirstSound(WaveFileReader reader, float silenceThreshold, int chunkSizeMs, int bytesPerMillisecond)
        {
            int position = 0;
            int bytesPerChunk = bytesPerMillisecond * chunkSizeMs;
            var buffer = new byte[bytesPerChunk];

            while (reader.Position < reader.Length)
            {
                int bytesRead = reader.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                float rms = CalculateRMS(buffer, bytesRead, reader.WaveFormat);
                if (20 * Math.Log10(rms) >= silenceThreshold)
                {
                    break;
                }

                position += chunkSizeMs;
            }

            // Reset the reader position for further processing
            reader.Position = 0;
            return position;
        }

        private static int GetPositionOfLastSound(WaveFileReader reader, float silenceThreshold, int chunkSizeMs, int bytesPerMillisecond)
        {
            int position = (int)(reader.Length / bytesPerMillisecond); // Total length in ms
            int bytesPerChunk = bytesPerMillisecond * chunkSizeMs;
            var buffer = new byte[bytesPerChunk];

            // Start from the end and move backwards
            reader.Position = reader.Length - bytesPerChunk;

            while (reader.Position > 0)
            {
                int bytesRead = reader.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                float rms = CalculateRMS(buffer, bytesRead, reader.WaveFormat);
                if (20 * Math.Log10(rms) >= silenceThreshold)
                {
                    break;
                }

                position -= chunkSizeMs;
                reader.Position = Math.Max(0, reader.Position - 2 * bytesPerChunk); // Move back two chunks
            }

            return position;
        }

        private static float CalculateRMS(byte[] buffer, int bytesRead, WaveFormat format)
        {
            double sumSquares = 0;
            int bytesPerSample = format.BitsPerSample / 8;
            int samples = bytesRead / bytesPerSample;

            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * bytesPerSample);
                float sample32 = sample / 32768f; // Convert to 32-bit float
                sumSquares += sample32 * sample32;
            }

            return (float)Math.Sqrt(sumSquares / samples);
        }
    }

}
