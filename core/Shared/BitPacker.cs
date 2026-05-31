namespace operation_vote.Shared
{
    public static class BitPacker
    {
        /// <summary>
        /// Combines a single leading bit and a raw byte array into an unaligned bit-packed stream.
        /// </summary>
        public static byte[] Pack(byte startingBit, byte[] data)
        {
            int totalBits = 1 + (data.Length * 8);
            int totalBytes = (totalBits + 7) / 8;
            byte[] output = new byte[totalBytes];

            if (startingBit == 1)
            {
                output[0] |= 0x80;
            }

            for (int i = 0; i < data.Length; i++)
            {
                int bitIndex = 1 + (i * 8);
                int byteOffset = bitIndex / 8;
                int shiftOffset = bitIndex % 8;

                output[byteOffset] |= (byte)(data[i] >> shiftOffset);
                
                if (byteOffset + 1 < output.Length)
                {
                    output[byteOffset + 1] |= (byte)(data[i] << (8 - shiftOffset));
                }
            }

            return output;
        }

        /// <summary>
        /// Decodes an unaligned bit-packed stream into a leading bit and its original byte array.
        /// </summary>
        public static (byte Prefix, byte[] Payload) Unpack(byte[] data)
        {
            if (data == null || data.Length == 0) return (0, Array.Empty<byte>());

            byte prefix = (byte)((data[0] & 0x80) >> 7);

            int totalPayloadBits = (data.Length * 8) - 1;
            int targetedBytesCount = totalPayloadBits / 8;
            
            byte[] payload = new byte[targetedBytesCount];

            for (int i = 0; i < payload.Length; i++)
            {
                byte currentPart = (byte)(data[i] << 1);
                byte nextPart = (byte)((data[i + 1] & 0x80) >> 7);
                payload[i] = (byte)(currentPart | nextPart);
            }

            return (prefix, payload);
        }
    }
}