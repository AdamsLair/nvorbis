﻿/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2013, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace NVorbis.Ogg
{
    [System.Diagnostics.DebuggerTypeProxy(typeof(PacketReader.DebugView))]
    class PacketReader : IPacketProvider
    {
        class DebugView
        {
            PacketReader _reader;

            public DebugView(PacketReader reader)
            {
                if (reader == null) throw new ArgumentNullException("reader");
                _reader = reader;
            }

            public ContainerReader Container { get { return _reader._container; } }
            public int StreamSerial { get { return _reader._streamSerial; } }
            public bool EndOfStreamFound { get { return _reader._eosFound; } }

            public int CurrentPacketIndex
            {
                get
                {
                    if (_reader._current == null) return -1;
                    return Array.IndexOf(Packets, _reader._current);
                }
            }

            Packet _last, _first;
            Packet[] _packetList = new Packet[0];
            public Packet[] Packets
            {
                get
                {
                    if (_reader._last == _last && _reader._first == _first)
                    {
                        return _packetList;
                    }

                    _last = _reader._last;
                    _first = _reader._first;

                    var packets = new List<Packet>();
                    var node = _first;
                    while (node != null)
                    {
                        packets.Add(node);
                        node = node.Next;
                    }
                    _packetList = packets.ToArray();
                    return _packetList;
                }
            }
        }

        ContainerReader _container;
        int _streamSerial;
        bool _eosFound;

        Packet _first, _current, _last;

        object _packetLock = new object();

        internal PacketReader(ContainerReader container, int streamSerial)
        {
            _container = container;
            _streamSerial = streamSerial;
        }

        public void Dispose()
        {
            _eosFound = true;

            _container.DisposePacketReader(this);
            _container = null;

            _current = null;

            if (_first != null)
            {
                var node = _first;
                _first = null;
                while (node.Next != null)
                {
                    var temp = node.Next;
                    node.Next = null;
                    node = temp;
                    node.Prev = null;
                }
                node = null;
            }

            _last = null;
        }

        internal void AddPacket(Packet packet)
        {
            lock (_packetLock)
            {
                // if the packet is a resync, it cannot be a continuation...
                if (packet.IsResync)
                {
                    packet.IsContinuation = false;
                    if (_last != null) _last.IsContinued = false;
                }

                if (packet.IsContinuation)
                {
                    // if we get here, the stream is invalid if there isn't a previous packet
                    if (_last == null) throw new InvalidDataException();

                    // if the last packet isn't continued, something is wrong
                    if (!_last.IsContinued) throw new InvalidDataException();

                    _last.MergeWith(packet);
                    _last.IsContinued = packet.IsContinued;
                }
                else
                {
                    var p = packet as Packet;
                    if (p == null) throw new ArgumentException("Wrong packet datatype", "packet");

                    if (_first == null)
                    {
                        // this is the first packet to add, so just set first & last to point at it
                        _first = p;
                        _last = p;
                    }
                    else
                    {
                        // swap the new packet in to the last position (remember, we're doubly-linked)
                        _last = ((p.Prev = _last).Next = p);
                    }
                }

                _eosFound |= packet.IsEndOfStream;
            }
        }

        public int StreamSerial
        {
            get { return _streamSerial; }
        }

        public long ContainerBits
        {
            get;
            set;
        }

        public bool CanSeek
        {
            get { return _container.CanSeek; }
        }

        bool EnsurePackets()
        {
            do
            {
                lock (_packetLock)
                {
                    // don't bother reading more packets unless we actually need them
                    if (_last != null && !_last.IsContinued && _current != _last) return true;
                }

                if (!_container.GatherNextPage(_streamSerial))
                {
                    // not technically true, but because the container doesn't have any more pages, it might as well be
                    _eosFound = true;
                    return false;
                }

                lock (_packetLock)
                {
                    // if we've read the entire stream, do some further checking...
                    if (_eosFound)
                    {
                        // make sure the last packet read isn't continued... (per the spec, if the last packet is a partial, ignore it)
                        // if _last is null, something has gone horribly wrong (i.e., that shouldn't happen)
                        if (_last.IsContinued)
                        {
                            _last = _last.Prev;
                            _last.Next.Prev = null;
                            _last.Next = null;
                        }

                        // if our "current" packet is the same as the "last" packet, we're done
                        // _last won't be null here
                        if (_current == _last) return false;
                    }
                }
            } while (true);
        }

        // This is fast path... don't make the caller wait if we can help it...
        public DataPacket GetNextPacket()
        {
            // make sure we have enough packets... if we're at the end of the stream, return null
            if (!EnsurePackets()) return null;

            Packet packet;
            lock (_packetLock)
            {
                // "current" is always set to the packet previous to the one about to be returned...
                if (_current == null)
                {
                    packet = (_current = _first);
                }
                else
                {
                    packet = (_current = _current.Next);
                }
            }

            if (packet.IsContinued) throw new InvalidDataException();

            // make sure the packet is ready for "playback"
            packet.Reset();

            return packet;
        }

        public DataPacket PeekNextPacket()
        {
            Packet curPacket;
            lock (_packetLock)
            {
                // get the current packet
                curPacket = (_current ?? _first);

                // if we don't have one, we can't do anything...
                if (curPacket == null) return null;

                // if we have a next packet, go ahead and return it
                if (curPacket.Next != null)
                {
                    return curPacket.Next;
                }

                // if we've hit the end of the stream, we're done
                if (_eosFound) return null;
            }

            // finally, try to load more packets and just return the next one
            EnsurePackets();
            return curPacket.Next;
        }

        public void SeekToPacket(int index)
        {
            if (!CanSeek) throw new InvalidOperationException();

            // we won't worry about locking here since the only atomic operation is the assignment to _current
            _current = GetPacketByIndex(index).Prev;
        }

        Packet GetPacketByIndex(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");

            // don't lock since we're obviously not even started yet...
            while (_first == null)
            {
                if (_eosFound) throw new InvalidDataException();

                if (!_container.GatherNextPage(_streamSerial)) _eosFound = true;
            }

            var packet = _first;
            while (--index >= 0)
            {
                lock (_packetLock)
                {
                    if (packet.Next != null)
                    {
                        packet = packet.Next;
                        continue;
                    }

                    if (_eosFound) throw new ArgumentOutOfRangeException("index");
                }

                do
                {
                    if (!_container.GatherNextPage(_streamSerial)) _eosFound = true;

                    if (_eosFound) throw new ArgumentOutOfRangeException("index");
                } while (packet.Next == null);

                // go ahead and loop back to the locked section above...
                ++index;
            }

            return packet;
        }

        internal void ReadAllPages()
        {
            if (!CanSeek) throw new InvalidOperationException();

            // don't hold the lock any longer than we have to
            while (true)
            {
                lock (_packetLock)
                {
                    if (_eosFound) break;
                }

                if (!_container.GatherNextPage(_streamSerial)) _eosFound = true;
            }
        }

        internal DataPacket GetLastPacket()
        {
            ReadAllPages();

            return _last;
        }

        public int GetTotalPageCount()
        {
            ReadAllPages();

            // here we just count the number of times the page sequence number changes
            var cnt = 0;
            var lastPageSeqNo = 0;
            var packet = _first;
            while (packet != null)
            {
                if (packet.PageSequenceNumber != lastPageSeqNo)
                {
                    ++cnt;
                    lastPageSeqNo = packet.PageSequenceNumber;
                }
                packet = packet.Next;
            }
            return cnt;
        }

        public DataPacket GetPacket(int packetIndex)
        {
            var packet = GetPacketByIndex(packetIndex);
            packet.Reset();
            return packet;
        }

        public int FindPacket(long granulePos, Func<DataPacket, DataPacket, int> packetGranuleCountCallback)
        {
            // This will find which packet contains the granule position being requested.  It is basically a linear search.
            // Please note, the spec actually calls for a bisection search, but the result here should be the same.

            // don't look for any position before 0!
            if (granulePos < 0) throw new ArgumentOutOfRangeException("granulePos");

            // find the first packet with a higher GranulePosition than the requested value
            // this is safe to do because we'll get a whole page at a time...
            while (true)
            {
                lock (_packetLock)
                {
                    if (_last != null && _last.PageGranulePosition >= granulePos) break;
                    if (_eosFound)
                    {
                        // only throw an exception when our data is no good
                        if (_first == null)
                        {
                            throw new InvalidDataException();
                        }
                        return -1;
                    }
                }

                if (!_container.GatherNextPage(_streamSerial)) _eosFound = true;
            }

            // We now know the page of the last packet ends somewhere past the requested granule position...
            // search back until we find the first packet past the requested position
            // if we make it back to the beginning, return -1;

            var packet = _last;
            // if the last packet is continued, ignore it (the page granule count actually applies to the previous packet)
            while (packet.IsContinued)
            {
                packet = packet.Prev;
            }
            do
            {
                // if we don't have a granule count, it's a new packet and we need to calculate its count & position
                if (!packet.GranuleCount.HasValue)
                {
                    // fun part... make sure the packets are ready for "playback"
                    if (packet.Prev != null) packet.Prev.Reset();
                    packet.Reset();

                    // go ask the callback to calculate the granule count for this packet (given the surrounding packets)
                    packet.GranuleCount = packetGranuleCountCallback(packet, packet.Prev);

                    // if it's the last (or second-last in the stream) packet, or it's "Next" is continued, or the next packet is on the next page, just use the page granule position
                    if (packet == _last || (_eosFound && packet == _last.Prev) || packet.Next.IsContinued || packet.Next.PageSequenceNumber > packet.PageSequenceNumber)
                    {
                        // if the page's granule position is -1, something must be horribly wrong... (AddPacket should have addressed this above)
                        if (packet.PageGranulePosition == -1) throw new InvalidDataException();

                        // use the page's granule position
                        packet.GranulePosition = packet.PageGranulePosition;

                        // if it's the last packet in the stream, it's a partial...
                        if (packet == _last && _eosFound)
                        {
                            packet.GranuleCount = (int)(packet.PageGranulePosition - packet.Prev.PageGranulePosition);
                        }
                    }
                    else
                    {
                        // this packet's granule position is the next packet's position less the next packet's count (which should already be calculated)
                        packet.GranulePosition = packet.Next.GranulePosition - packet.Next.GranuleCount.Value;
                    }
                }

                // now we know what this packet's granule position is...
                if (packet.GranulePosition < granulePos)
                {
                    // we've found the packet previous to the one we need...
                    packet = packet.Next;
                    break;
                }

                // we didn't find the packet, so update and loop
                packet = packet.Prev;
            } while (packet != null);

            // if we didn't find the packet, something is wrong
            if (packet == null) return -1;

            // we found the packet, so now we just have to count back to the beginning and see what its index is...
            int idx = 0;
            while (packet.Prev != null)
            {
                packet = packet.Prev;
                ++idx;
            }
            return idx;
        }

        public long GetGranuleCount()
        {
            return GetLastPacket().PageGranulePosition;
        }
    }
}
