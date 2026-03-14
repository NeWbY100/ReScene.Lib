namespace RARLib.Decompression.PPMd;

/// <summary>
/// Memory sub-allocator for PPMd decompression.
/// Ported from unrar suballoc.cpp/hpp by Dmitry Shkarin.
/// </summary>
public class SubAllocator
{
    private const int N1 = 4, N2 = 4, N3 = 4;
    private const int N4 = (128 + 3 - 1 * N1 - 2 * N2 - 3 * N3) / 4;
    private const int N_INDEXES = N1 + N2 + N3 + N4;
    private const int UNIT_SIZE = 12;
    private const int FIXED_UNIT_SIZE = 12;

    private readonly byte[] _indx2Units = new byte[N_INDEXES];
    private readonly byte[] _units2Indx = new byte[128];
    private readonly int[] _freeListPos = new int[N_INDEXES];
    private byte _glueCount;

    private byte[]? _heap;
    private int _heapStart;
    private int _heapEnd;
    private int _loUnit;
    private int _hiUnit;
    private int _unitsStart;
    private int _fakeUnitsStart;
    private int _pText;

    private int _subAllocatorSize;

    public int PText => _pText;
    public int FakeUnitsStart => _fakeUnitsStart;
    public int HeapEnd => _heapEnd;

    public SubAllocator()
    {
        Clean();
    }

    public void Clean()
    {
        _subAllocatorSize = 0;
        _heap = null;
    }

    public bool StartSubAllocator(int saMB)
    {
        int size = saMB << 20;
        if (_subAllocatorSize == size)
            return true;

        StopSubAllocator();

        int allocSize = size / FIXED_UNIT_SIZE * UNIT_SIZE + 2 * UNIT_SIZE;
        _heap = new byte[allocSize];
        _heapStart = 0;
        _heapEnd = allocSize - UNIT_SIZE;
        _subAllocatorSize = size;
        return true;
    }

    public void StopSubAllocator()
    {
        if (_subAllocatorSize != 0)
        {
            _subAllocatorSize = 0;
            _heap = null;
        }
    }

    public void InitSubAllocator()
    {
        Array.Clear(_freeListPos);
        _pText = _heapStart;

        int size2 = FIXED_UNIT_SIZE * (_subAllocatorSize / 8 / FIXED_UNIT_SIZE * 7);
        int realSize2 = size2 / FIXED_UNIT_SIZE * UNIT_SIZE;
        int size1 = _subAllocatorSize - size2;
        int realSize1 = size1 / FIXED_UNIT_SIZE * UNIT_SIZE + UNIT_SIZE;

        _loUnit = _unitsStart = _heapStart + realSize1;
        _fakeUnitsStart = _heapStart + size1;
        _hiUnit = _loUnit + realSize2;

        int i, k;
        for (i = 0, k = 1; i < N1; i++, k++)
            _indx2Units[i] = (byte)k;
        for (k++; i < N1 + N2; i++, k += 2)
            _indx2Units[i] = (byte)k;
        for (k++; i < N1 + N2 + N3; i++, k += 3)
            _indx2Units[i] = (byte)k;
        for (k++; i < N1 + N2 + N3 + N4; i++, k += 4)
            _indx2Units[i] = (byte)k;

        for (_glueCount = 0, k = 0, i = 0; k < 128; k++)
        {
            if (_indx2Units[i] < k + 1)
                i++;
            _units2Indx[k] = (byte)i;
        }
    }

    private int U2B(int nu) => UNIT_SIZE * nu;

    public int AllocContext()
    {
        if (_hiUnit != _loUnit)
        {
            _hiUnit -= UNIT_SIZE;
            return _hiUnit;
        }
        if (_freeListPos[0] != 0)
            return RemoveNode(0);
        return AllocUnitsRare(0);
    }

    public int AllocUnits(int nu)
    {
        int indx = _units2Indx[nu - 1];
        if (_freeListPos[indx] != 0)
            return RemoveNode(indx);

        int retVal = _loUnit;
        _loUnit += U2B(_indx2Units[indx]);
        if (_loUnit <= _hiUnit)
            return retVal;

        _loUnit -= U2B(_indx2Units[indx]);
        return AllocUnitsRare(indx);
    }

    public int ExpandUnits(int oldPtr, int oldNU)
    {
        int i0 = _units2Indx[oldNU - 1];
        int i1 = _units2Indx[oldNU];
        if (i0 == i1)
            return oldPtr;

        int ptr = AllocUnits(oldNU + 1);
        if (ptr != 0 && _heap != null)
        {
            Array.Copy(_heap, oldPtr, _heap, ptr, U2B(oldNU));
            InsertNode(oldPtr, i0);
        }
        return ptr;
    }

    public int ShrinkUnits(int oldPtr, int oldNU, int newNU)
    {
        int i0 = _units2Indx[oldNU - 1];
        int i1 = _units2Indx[newNU - 1];
        if (i0 == i1)
            return oldPtr;

        if (_freeListPos[i1] != 0)
        {
            int ptr = RemoveNode(i1);
            if (_heap != null)
                Array.Copy(_heap, oldPtr, _heap, ptr, U2B(newNU));
            InsertNode(oldPtr, i0);
            return ptr;
        }
        else
        {
            SplitBlock(oldPtr, i0, i1);
            return oldPtr;
        }
    }

    public void FreeUnits(int ptr, int oldNU)
    {
        InsertNode(ptr, _units2Indx[oldNU - 1]);
    }

    private void InsertNode(int p, int indx)
    {
        if (_heap == null) return;
        // Store next pointer at position p
        WriteInt(_heap, p, _freeListPos[indx]);
        _freeListPos[indx] = p;
    }

    private int RemoveNode(int indx)
    {
        if (_heap == null) return 0;
        int retVal = _freeListPos[indx];
        _freeListPos[indx] = ReadInt(_heap, retVal);
        return retVal;
    }

    private void SplitBlock(int pv, int oldIndx, int newIndx)
    {
        int uDiff = _indx2Units[oldIndx] - _indx2Units[newIndx];
        int p = pv + U2B(_indx2Units[newIndx]);
        int i = _units2Indx[uDiff - 1];
        if (_indx2Units[i] != uDiff)
        {
            i--;
            InsertNode(p, i);
            p += U2B(_indx2Units[i]);
            uDiff -= _indx2Units[i];
        }
        InsertNode(p, _units2Indx[uDiff - 1]);
    }

    private int AllocUnitsRare(int indx)
    {
        if (_glueCount == 0)
        {
            _glueCount = 255;
            GlueFreeBlocks();
            if (_freeListPos[indx] != 0)
                return RemoveNode(indx);
        }

        int i = indx;
        do
        {
            if (++i == N_INDEXES)
            {
                _glueCount--;
                int size = U2B(_indx2Units[indx]);
                int j = FIXED_UNIT_SIZE * _indx2Units[indx];
                if (_fakeUnitsStart - _pText > j)
                {
                    _fakeUnitsStart -= j;
                    _unitsStart -= size;
                    return _unitsStart;
                }
                return 0;
            }
        } while (_freeListPos[i] == 0);

        int retVal = RemoveNode(i);
        SplitBlock(retVal, i, indx);
        return retVal;
    }

    private void GlueFreeBlocks()
    {
        if (_heap == null) return;

        // Simple implementation - just clear free lists for now
        // Full implementation would merge adjacent free blocks
        if (_loUnit != _hiUnit && _loUnit < _heap.Length)
            _heap[_loUnit] = 0;

        // This is a simplified version - full version would merge blocks
        Array.Clear(_freeListPos);
    }

    public int GetAllocatedMemory() => _subAllocatorSize;

    public bool IncrementPText()
    {
        _pText++;
        return _pText < _fakeUnitsStart;
    }

    public void SetPTextByte(byte value)
    {
        if (_heap != null && _pText < _heap.Length)
            _heap[_pText] = value;
    }

    public byte GetByte(int offset)
    {
        if (_heap == null || offset < 0 || offset >= _heap.Length)
            return 0;
        return _heap[offset];
    }

    public void SetByte(int offset, byte value)
    {
        if (_heap != null && offset >= 0 && offset < _heap.Length)
            _heap[offset] = value;
    }

    public ushort GetUShort(int offset)
    {
        if (_heap == null || offset < 0 || offset + 1 >= _heap.Length)
            return 0;
        return (ushort)(_heap[offset] | (_heap[offset + 1] << 8));
    }

    public void SetUShort(int offset, ushort value)
    {
        if (_heap != null && offset >= 0 && offset + 1 < _heap.Length)
        {
            _heap[offset] = (byte)value;
            _heap[offset + 1] = (byte)(value >> 8);
        }
    }

    public int GetInt(int offset)
    {
        if (_heap == null || offset < 0 || offset + 3 >= _heap.Length)
            return 0;
        return ReadInt(_heap, offset);
    }

    public void SetInt(int offset, int value)
    {
        if (_heap != null && offset >= 0 && offset + 3 < _heap.Length)
            WriteInt(_heap, offset, value);
    }

    private static int ReadInt(byte[] buffer, int offset)
    {
        return buffer[offset] |
               (buffer[offset + 1] << 8) |
               (buffer[offset + 2] << 16) |
               (buffer[offset + 3] << 24);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
