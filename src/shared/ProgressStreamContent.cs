using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace NubeZero.Shared
{
    public sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly Action<long, long> _progress;

        public ProgressStreamContent(Stream source, Action<long, long> progress)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _progress = progress;
        }

        protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
        {
            long total = _source.CanSeek ? _source.Length - _source.Position : -1;
            long transferred = 0;
            byte[] buffer = new byte[81920];
            int count;

            while ((count = await _source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                transferred += count;
                _progress?.Invoke(transferred, total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_source.CanSeek)
            {
                length = _source.Length - _source.Position;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}