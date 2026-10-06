using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace NubeZero.Shared
{
    public sealed class EncryptedProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly byte[] _key;
        private readonly long _plaintextLength;
        private readonly Action<long, long> _progress;

        public EncryptedProgressStreamContent(Stream source, byte[] key, Action<long, long> progress, long knownLength = -1)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _progress = progress;

            if (knownLength >= 0)
                _plaintextLength = knownLength;
            else if (_source.CanSeek)
                _plaintextLength = _source.Length - _source.Position;
            else
                _plaintextLength = -1;

            if (_plaintextLength >= 0)
                Headers.ContentLength = FileEncryptionService.GetEncryptedLength(_plaintextLength);
        }

        protected override Task SerializeToStreamAsync(Stream target, TransportContext context)
        {
            return FileEncryptionService.EncryptAsync(_source, target, _key, _progress);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_plaintextLength >= 0)
            {
                length = FileEncryptionService.GetEncryptedLength(_plaintextLength);
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
