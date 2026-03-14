namespace RARLib.Decompression.PPMd;

/// <summary>
/// PPMd prediction model for RAR decompression.
/// Ported from unrar model.cpp/hpp by Dmitry Shkarin.
/// </summary>
public class ModelPPM
{
    private const int INT_BITS = 7;
    private const int PERIOD_BITS = 7;
    private const int TOT_BITS = INT_BITS + PERIOD_BITS;
    private const int INTERVAL = 1 << INT_BITS;
    private const int BIN_SCALE = 1 << TOT_BITS;
    private const int MAX_FREQ = 124;

    // Structure offsets and sizes
    private const int STATE_SIZE = 6;      // Symbol(1) + Freq(1) + Successor(4)

    // SEE2 context arrays
    private readonly ushort[,] _see2Cont = new ushort[25, 16];
    private readonly byte[,] _see2Shift = new byte[25, 16];
    private readonly byte[,] _see2Count = new byte[25, 16];

    private int _minContext;
    private int _maxContext;
    private int _foundState;

    private int _numMasked;
    private int _initEsc;
    private int _orderFall;
    private int _maxOrder;
    private int _runLength;
    private int _initRL;

    private readonly byte[] _charMask = new byte[256];
    private readonly byte[] _ns2Indx = new byte[256];
    private readonly byte[] _ns2BSIndx = new byte[256];
    private readonly byte[] _hb2Flag = new byte[256];
    private byte _escCount;
    private byte _prevSuccess;
    private byte _hiBitsFlag;
    private readonly ushort[,] _binSumm = new ushort[128, 64];

    private readonly RangeCoder _coder = new();
    private readonly SubAllocator _subAlloc = new();

    private static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];
    private static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];

    public ModelPPM()
    {
        _minContext = 0;
        _maxContext = 0;
    }

    /// <summary>
    /// Initializes the PPMd decoder.
    /// </summary>
    public bool DecodeInit(byte[] data, ref int offset, ref int escChar)
    {
        if (offset >= data.Length)
            return false;

        int maxOrder = data[offset++];
        bool reset = (maxOrder & 0x20) != 0;

        int maxMB = 0;
        if (reset)
        {
            if (offset >= data.Length)
                return false;
            maxMB = data[offset++];
        }
        else if (_subAlloc.GetAllocatedMemory() == 0)
        {
            return false;
        }

        if ((maxOrder & 0x40) != 0)
        {
            if (offset >= data.Length)
                return false;
            escChar = data[offset++];
        }

        _coder.InitDecoder(data, offset);

        if (reset)
        {
            maxOrder = (maxOrder & 0x1F) + 1;
            if (maxOrder > 16)
                maxOrder = 16 + (maxOrder - 16) * 3;
            if (maxOrder == 1)
            {
                _subAlloc.StopSubAllocator();
                return false;
            }
            _subAlloc.StartSubAllocator(maxMB + 1);
            StartModelRare(maxOrder);
        }

        return _minContext != 0;
    }

    /// <summary>
    /// Decodes a single character.
    /// </summary>
    public int DecodeChar()
    {
        if (_minContext <= _subAlloc.PText || _minContext > _subAlloc.HeapEnd)
            return -1;

        int numStats = GetContextNumStats(_minContext);
        if (numStats != 1)
        {
            int stats = GetContextStats(_minContext);
            if (stats <= _subAlloc.PText || stats > _subAlloc.HeapEnd)
                return -1;
            if (!DecodeSymbol1())
                return -1;
        }
        else
        {
            DecodeBinSymbol();
        }

        _coder.Decode();

        while (_foundState == 0)
        {
            _coder.Normalize();
            do
            {
                _orderFall++;
                _minContext = GetContextSuffix(_minContext);
                if (_minContext <= _subAlloc.PText || _minContext > _subAlloc.HeapEnd)
                    return -1;
            } while (GetContextNumStats(_minContext) == _numMasked);

            if (!DecodeSymbol2())
                return -1;
            _coder.Decode();
        }

        int symbol = GetStateSymbol(_foundState);

        if (_orderFall == 0 && GetStateSuccessor(_foundState) > _subAlloc.PText)
        {
            _minContext = _maxContext = GetStateSuccessor(_foundState);
        }
        else
        {
            UpdateModel();
            if (_escCount == 0)
                ClearMask();
        }

        _coder.Normalize();
        return symbol;
    }

    private void StartModelRare(int maxOrder)
    {
        _escCount = 1;
        _maxOrder = maxOrder;
        RestartModelRare();

        _ns2BSIndx[0] = 0;
        _ns2BSIndx[1] = 2;
        for (int i = 2; i < 11; i++)
            _ns2BSIndx[i] = 4;
        for (int i = 11; i < 256; i++)
            _ns2BSIndx[i] = 6;

        for (int i = 0; i < 3; i++)
            _ns2Indx[i] = (byte)i;
        for (int m = 3, k = 1, step = 1; m < 256; m++)
        {
            _ns2Indx[m] = (byte)(m < 3 ? m : (k < 256 ? k : 255));
            if (--k == 0)
            {
                step++;
                k = step;
                if (m >= 3)
                    _ns2Indx[m] = (byte)Math.Min(_ns2Indx[m - 1] + 1, 255);
            }
        }

        // Fix ns2Indx initialization
        for (int i = 0, m = 0, k = 1, step = 1; i < 256; i++)
        {
            _ns2Indx[i] = (byte)m;
            if (--k == 0)
            {
                k = ++step;
                m++;
            }
        }

        for (int i = 0; i < 0x40; i++)
            _hb2Flag[i] = 0;
        for (int i = 0x40; i < 0x100; i++)
            _hb2Flag[i] = 0x08;
    }

    private void RestartModelRare()
    {
        Array.Clear(_charMask);
        _subAlloc.InitSubAllocator();
        _initRL = -(_maxOrder < 12 ? _maxOrder : 12) - 1;

        _minContext = _maxContext = _subAlloc.AllocContext();
        if (_minContext == 0)
            return;

        SetContextSuffix(_minContext, 0);
        _orderFall = _maxOrder;
        SetContextNumStats(_minContext, 256);
        SetContextSummFreq(_minContext, 257);

        _foundState = _subAlloc.AllocUnits(128); // 256/2 states
        if (_foundState == 0)
            return;

        SetContextStats(_minContext, _foundState);

        for (_runLength = _initRL, _prevSuccess = 0; _foundState != 0;)
        {
            int state = GetContextStats(_minContext);
            for (int i = 0; i < 256; i++)
            {
                SetStateSymbol(state + i * STATE_SIZE, (byte)i);
                SetStateFreq(state + i * STATE_SIZE, 1);
                SetStateSuccessor(state + i * STATE_SIZE, 0);
            }
            break;
        }

        // Initialize BinSumm
        for (int i = 0; i < 128; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                for (int m = 0; m < 64; m += 8)
                {
                    _binSumm[i, k + m] = (ushort)(BIN_SCALE - InitBinEsc[k] / (i + 2));
                }
            }
        }

        // Initialize SEE2 contexts
        for (int i = 0; i < 25; i++)
        {
            for (int k = 0; k < 16; k++)
            {
                int initVal = 5 * i + 10;
                _see2Shift[i, k] = (byte)(PERIOD_BITS - 4);
                _see2Cont[i, k] = (ushort)(initVal << _see2Shift[i, k]);
                _see2Count[i, k] = 4;
            }
        }
    }

    private void DecodeBinSymbol()
    {
        int state = GetContextOneState(_minContext);
        byte stateSymbol = GetStateSymbol(state);
        byte stateFreq = GetStateFreq(state);

        _hiBitsFlag = _hb2Flag[_foundState != 0 ? GetStateSymbol(_foundState) : 0];

        int suffix = GetContextSuffix(_minContext);
        int suffixNumStats = suffix != 0 ? GetContextNumStats(suffix) : 1;

        int idx = stateFreq - 1;
        int idx2 = _prevSuccess + _ns2BSIndx[suffixNumStats - 1] + _hiBitsFlag + 2 * _hb2Flag[stateSymbol] + ((_runLength >> 26) & 0x20);
        if (idx2 >= 64) idx2 = 63;

        ushort bs = _binSumm[idx, idx2];

        if (_coder.GetCurrentShiftCount(TOT_BITS) < bs)
        {
            _foundState = state;
            if (stateFreq < 128)
                SetStateFreq(state, (byte)(stateFreq + 1));
            _coder.LowCount = 0;
            _coder.HighCount = bs;
            _binSumm[idx, idx2] = (ushort)(bs + INTERVAL - GetMean(bs, PERIOD_BITS, 2));
            _prevSuccess = 1;
            _runLength++;
        }
        else
        {
            _coder.LowCount = bs;
            _binSumm[idx, idx2] = (ushort)(bs - GetMean(bs, PERIOD_BITS, 2));
            _coder.HighCount = BIN_SCALE;
            _initEsc = ExpEscape[bs >> 10];
            _numMasked = 1;
            _charMask[stateSymbol] = _escCount;
            _prevSuccess = 0;
            _foundState = 0;
        }
    }

    private bool DecodeSymbol1()
    {
        _coder.Scale = (uint)GetContextSummFreq(_minContext);
        int stats = GetContextStats(_minContext);
        int count = _coder.GetCurrentCount();

        if (count >= (int)_coder.Scale)
            return false;

        int p = stats;
        int hiCnt = GetStateFreq(p);

        if (count < hiCnt)
        {
            _coder.HighCount = (uint)hiCnt;
            _prevSuccess = (byte)((2 * hiCnt > _coder.Scale) ? 1 : 0);
            _runLength += _prevSuccess;
            _foundState = p;
            hiCnt += 4;
            SetStateFreq(p, (byte)Math.Min(hiCnt, 255));
            int summFreq = GetContextSummFreq(_minContext) + 4;
            SetContextSummFreq(_minContext, (ushort)summFreq);
            if (hiCnt > MAX_FREQ)
                Rescale();
            _coder.LowCount = 0;
            return true;
        }

        if (_foundState == 0)
            return false;

        _prevSuccess = 0;
        int numStats = GetContextNumStats(_minContext);
        int i = numStats - 1;

        while ((hiCnt += GetStateFreq(p += STATE_SIZE)) <= count)
        {
            if (--i == 0)
            {
                _hiBitsFlag = _hb2Flag[GetStateSymbol(_foundState)];
                _coder.LowCount = (uint)hiCnt;
                _charMask[GetStateSymbol(p)] = _escCount;
                i = (_numMasked = numStats) - 1;
                _foundState = 0;
                do
                {
                    _charMask[GetStateSymbol(p -= STATE_SIZE)] = _escCount;
                } while (--i > 0);
                _coder.HighCount = _coder.Scale;
                return true;
            }
        }

        _coder.LowCount = (uint)(hiCnt - GetStateFreq(p));
        _coder.HighCount = (uint)hiCnt;
        Update1(p);
        return true;
    }

    private bool DecodeSymbol2()
    {
        int numStats = GetContextNumStats(_minContext);
        int diff = numStats - _numMasked;

        // Make escape frequency
        MakeEscFreq2(diff, out int see2Idx1, out int see2Idx2);

        int stats = GetContextStats(_minContext);
        int p = stats - STATE_SIZE;
        int hiCnt = 0;
        int[] ps = new int[256];
        int ppsCount = 0;

        int i = diff;
        do
        {
            do
            {
                p += STATE_SIZE;
            } while (_charMask[GetStateSymbol(p)] == _escCount);

            hiCnt += GetStateFreq(p);
            if (ppsCount < 256)
                ps[ppsCount++] = p;
        } while (--i > 0);

        _coder.Scale += (uint)hiCnt;
        int count = _coder.GetCurrentCount();

        if (count >= (int)_coder.Scale)
            return false;

        p = ps[0];
        int ppsIdx = 0;

        if (count < hiCnt)
        {
            hiCnt = 0;
            while ((hiCnt += GetStateFreq(p)) <= count)
            {
                ppsIdx++;
                if (ppsIdx >= ppsCount)
                    return false;
                p = ps[ppsIdx];
            }
            _coder.LowCount = (uint)(hiCnt - GetStateFreq(p));
            _coder.HighCount = (uint)hiCnt;
            See2Update(see2Idx1, see2Idx2);
            Update2(p);
        }
        else
        {
            _coder.LowCount = (uint)hiCnt;
            _coder.HighCount = _coder.Scale;
            i = diff;
            ppsIdx = 0;
            do
            {
                if (ppsIdx >= ppsCount)
                    return false;
                _charMask[GetStateSymbol(ps[ppsIdx])] = _escCount;
                ppsIdx++;
            } while (--i > 0);
            _see2Cont[see2Idx1, see2Idx2] += (ushort)_coder.Scale;
            _numMasked = numStats;
        }
        return true;
    }

    private void Update1(int p)
    {
        _foundState = p;
        byte freq = GetStateFreq(p);
        SetStateFreq(p, (byte)(freq + 4));
        int summFreq = GetContextSummFreq(_minContext) + 4;
        SetContextSummFreq(_minContext, (ushort)summFreq);

        // Swap if needed
        int prevP = p - STATE_SIZE;
        if (freq + 4 > GetStateFreq(prevP))
        {
            SwapStates(p, prevP);
            _foundState = prevP;
            if (GetStateFreq(prevP) > MAX_FREQ)
                Rescale();
        }
    }

    private void Update2(int p)
    {
        _foundState = p;
        byte freq = GetStateFreq(p);
        SetStateFreq(p, (byte)(freq + 4));
        int summFreq = GetContextSummFreq(_minContext) + 4;
        SetContextSummFreq(_minContext, (ushort)summFreq);
        if (freq + 4 > MAX_FREQ)
            Rescale();
        _escCount++;
        _runLength = _initRL;
    }

    private void Rescale()
    {
        // Simplified rescale - halve all frequencies
        int stats = GetContextStats(_minContext);
        int numStats = GetContextNumStats(_minContext);
        int summFreq = 0;

        for (int i = 0; i < numStats; i++)
        {
            int state = stats + i * STATE_SIZE;
            byte freq = GetStateFreq(state);
            freq = (byte)((freq + 1) >> 1);
            if (freq == 0) freq = 1;
            SetStateFreq(state, freq);
            summFreq += freq;
        }

        SetContextSummFreq(_minContext, (ushort)(summFreq + numStats));
    }

    private void MakeEscFreq2(int diff, out int idx1, out int idx2)
    {
        int numStats = GetContextNumStats(_minContext);
        if (numStats != 256)
        {
            idx1 = _ns2Indx[diff - 1];
            int suffix = GetContextSuffix(_minContext);
            int suffixNumStats = suffix != 0 ? GetContextNumStats(suffix) : 1;
            idx2 = (diff < suffixNumStats - numStats ? 1 : 0) +
                   (GetContextSummFreq(_minContext) < 11 * numStats ? 2 : 0) +
                   (_numMasked > diff ? 4 : 0) +
                   _hiBitsFlag;
            if (idx2 >= 16) idx2 = 15;
            _coder.Scale = See2GetMean(idx1, idx2);
        }
        else
        {
            idx1 = 0;
            idx2 = 0;
            _coder.Scale = 1;
        }
    }

    private uint See2GetMean(int idx1, int idx2)
    {
        int retVal = _see2Cont[idx1, idx2] >> _see2Shift[idx1, idx2];
        _see2Cont[idx1, idx2] -= (ushort)retVal;
        return (uint)(retVal + (retVal == 0 ? 1 : 0));
    }

    private void See2Update(int idx1, int idx2)
    {
        if (_see2Shift[idx1, idx2] < PERIOD_BITS && --_see2Count[idx1, idx2] == 0)
        {
            _see2Cont[idx1, idx2] += _see2Cont[idx1, idx2];
            _see2Count[idx1, idx2] = (byte)(3 << _see2Shift[idx1, idx2]++);
        }
    }

    private void UpdateModel()
    {
        // Simplified update model - just update the current context
        if (_foundState == 0)
            return;

        byte symbol = GetStateSymbol(_foundState);
        _subAlloc.SetPTextByte(symbol);
        if (!_subAlloc.IncrementPText())
        {
            RestartModelRare();
            _escCount = 0;
        }
    }

    private void ClearMask()
    {
        _escCount = 1;
        Array.Clear(_charMask);
    }

    private static int GetMean(int summ, int shift, int round)
    {
        return (summ + (1 << (shift - round))) >> shift;
    }

    // Context accessors
    private int GetContextNumStats(int ctx)
    {
        return _subAlloc.GetUShort(ctx);
    }

    private void SetContextNumStats(int ctx, int value)
    {
        _subAlloc.SetUShort(ctx, (ushort)value);
    }

    private int GetContextSummFreq(int ctx)
    {
        return _subAlloc.GetUShort(ctx + 2);
    }

    private void SetContextSummFreq(int ctx, ushort value)
    {
        _subAlloc.SetUShort(ctx + 2, value);
    }

    private int GetContextStats(int ctx)
    {
        return _subAlloc.GetInt(ctx + 4);
    }

    private void SetContextStats(int ctx, int value)
    {
        _subAlloc.SetInt(ctx + 4, value);
    }

    private static int GetContextOneState(int ctx)
    {
        return ctx + 2; // OneState starts at offset 2 in the union
    }

    private int GetContextSuffix(int ctx)
    {
        return _subAlloc.GetInt(ctx + 8);
    }

    private void SetContextSuffix(int ctx, int value)
    {
        _subAlloc.SetInt(ctx + 8, value);
    }

    // State accessors
    private byte GetStateSymbol(int state)
    {
        return _subAlloc.GetByte(state);
    }

    private void SetStateSymbol(int state, byte value)
    {
        _subAlloc.SetByte(state, value);
    }

    private byte GetStateFreq(int state)
    {
        return _subAlloc.GetByte(state + 1);
    }

    private void SetStateFreq(int state, byte value)
    {
        _subAlloc.SetByte(state + 1, value);
    }

    private int GetStateSuccessor(int state)
    {
        return _subAlloc.GetInt(state + 2);
    }

    private void SetStateSuccessor(int state, int value)
    {
        _subAlloc.SetInt(state + 2, value);
    }

    private void SwapStates(int s1, int s2)
    {
        byte sym1 = GetStateSymbol(s1);
        byte freq1 = GetStateFreq(s1);
        int succ1 = GetStateSuccessor(s1);

        SetStateSymbol(s1, GetStateSymbol(s2));
        SetStateFreq(s1, GetStateFreq(s2));
        SetStateSuccessor(s1, GetStateSuccessor(s2));

        SetStateSymbol(s2, sym1);
        SetStateFreq(s2, freq1);
        SetStateSuccessor(s2, succ1);
    }

    public void CleanUp()
    {
        _subAlloc.StopSubAllocator();
        _subAlloc.StartSubAllocator(1);
        StartModelRare(2);
    }
}
