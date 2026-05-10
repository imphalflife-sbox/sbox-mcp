namespace SboxMcp.Bridge.Diagnostics;

internal sealed record LogEntry(
	long Seq,
	string Timestamp,   // ISO-8601
	string Level,
	string Logger,
	string Message,
	string Exception
);

internal sealed class LogRing
{
	private readonly object _lock = new();
	private readonly LogEntry[] _buf;
	private long _nextSeq;
	private int _count;

	public LogRing( int capacity )
	{
		if ( capacity <= 0 ) throw new ArgumentOutOfRangeException( nameof( capacity ) );
		_buf = new LogEntry[capacity];
	}

	public void Append( DateTime time, string level, string logger, string message, string exception )
	{
		lock ( _lock )
		{
			var entry = new LogEntry(
				_nextSeq,
				time.ToString( "o" ),
				level, logger, message, exception );
			_buf[(int)(_nextSeq % _buf.Length)] = entry;
			_nextSeq++;
			if ( _count < _buf.Length ) _count++;
		}
	}

	// Returns entries with Seq > since, capped at max. Empty array if nothing newer.
	public LogEntry[] Since( long since, int max )
	{
		lock ( _lock )
		{
			long oldest = _nextSeq - _count;
			long start = Math.Max( since + 1, oldest );
			long avail = _nextSeq - start;
			if ( avail <= 0 ) return Array.Empty<LogEntry>();
			int take = (int)Math.Min( avail, max );
			var result = new LogEntry[take];
			for ( int i = 0; i < take; i++ )
			{
				long s = start + i;
				result[i] = _buf[(int)(s % _buf.Length)];
			}
			return result;
		}
	}

	// Returns the most-recent N entries (oldest-first within the slice). Use when the
	// caller wants "latest", regardless of where the cursor was. Avoids the foot-gun
	// where since=-1 + a small max returns the oldest slice on a full ring.
	public LogEntry[] Tail( int max )
	{
		lock ( _lock )
		{
			if ( _count == 0 || max <= 0 ) return Array.Empty<LogEntry>();
			int take = Math.Min( _count, max );
			long start = _nextSeq - take;
			var result = new LogEntry[take];
			for ( int i = 0; i < take; i++ )
			{
				long s = start + i;
				result[i] = _buf[(int)(s % _buf.Length)];
			}
			return result;
		}
	}

	public void Reset()
	{
		lock ( _lock )
		{
			Array.Clear( _buf, 0, _buf.Length );
			_count = 0;
			// _nextSeq is intentionally NOT reset — keeping seq monotonic across resets
			// preserves cursor uniqueness for any callers that cached a cursor across
			// the reset boundary; they'll just see "no entries" until new ones arrive.
		}
	}

	public long NextSeq { get { lock ( _lock ) return _nextSeq; } }
	public int Count { get { lock ( _lock ) return _count; } }
	public int Capacity => _buf.Length;
}
