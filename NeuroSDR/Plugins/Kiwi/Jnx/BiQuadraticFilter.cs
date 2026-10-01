namespace NeuroSDR.Plugins.Kiwi.Jnx;

internal sealed class BiQuadraticFilter
{
    public enum FilterType
    {
        Bandpass, Lowpass, Highpass, Notch, Peak, LowShelf, HighShelf
    }

    double _a0, _a1, _a2, _b0, _b1, _b2;
    double _x1, _x2, _y1, _y2;
    double _gainAbs;
    FilterType _type;
    double _centerFreq, _sampleRate, _q, _gainDb;

    public BiQuadraticFilter() { }

    public BiQuadraticFilter(FilterType type, double centerFreq, double sampleRate, double q, double gainDb = 0) =>
        Configure(type, centerFreq, sampleRate, q, gainDb);

    public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

    public double Frequency => _centerFreq;

    public void Configure(FilterType type, double centerFreq, double sampleRate, double q, double gainDb = 0)
    {
        Reset();
        if (q == 0) q = 1e-9;
        _type = type;
        _sampleRate = sampleRate;
        _q = q;
        _gainDb = gainDb;
        Reconfigure(centerFreq);
    }

    public void Reconfigure(double cf)
    {
        _centerFreq = cf;
        _gainAbs = Math.Pow(10, _gainDb / 40);
        double omega = 2 * Math.PI * cf / _sampleRate;
        double sn = Math.Sin(omega);
        double cs = Math.Cos(omega);
        double alpha = sn / (2 * _q);
        double beta = Math.Sqrt(_gainAbs + _gainAbs);
        switch (_type)
        {
            case FilterType.Bandpass:
                _b0 = alpha; _b1 = 0; _b2 = -alpha;
                _a0 = 1 + alpha; _a1 = -2 * cs; _a2 = 1 - alpha;
                break;
            case FilterType.Lowpass:
                _b0 = (1 - cs) / 2; _b1 = 1 - cs; _b2 = (1 - cs) / 2;
                _a0 = 1 + alpha; _a1 = -2 * cs; _a2 = 1 - alpha;
                break;
            case FilterType.Highpass:
                _b0 = (1 + cs) / 2; _b1 = -(1 + cs); _b2 = (1 + cs) / 2;
                _a0 = 1 + alpha; _a1 = -2 * cs; _a2 = 1 - alpha;
                break;
            case FilterType.Notch:
                _b0 = 1; _b1 = -2 * cs; _b2 = 1;
                _a0 = 1 + alpha; _a1 = -2 * cs; _a2 = 1 - alpha;
                break;
            case FilterType.Peak:
                _b0 = 1 + alpha * _gainAbs; _b1 = -2 * cs; _b2 = 1 - alpha * _gainAbs;
                _a0 = 1 + alpha / _gainAbs; _a1 = -2 * cs; _a2 = 1 - alpha / _gainAbs;
                break;
            case FilterType.LowShelf:
                _b0 = _gainAbs * ((_gainAbs + 1) - (_gainAbs - 1) * cs + beta * sn);
                _b1 = 2 * _gainAbs * ((_gainAbs - 1) - (_gainAbs + 1) * cs);
                _b2 = _gainAbs * ((_gainAbs + 1) - (_gainAbs - 1) * cs - beta * sn);
                _a0 = (_gainAbs + 1) + (_gainAbs - 1) * cs + beta * sn;
                _a1 = -2 * ((_gainAbs - 1) + (_gainAbs + 1) * cs);
                _a2 = (_gainAbs + 1) + (_gainAbs - 1) * cs - beta * sn;
                break;
            case FilterType.HighShelf:
                _b0 = _gainAbs * ((_gainAbs + 1) + (_gainAbs - 1) * cs + beta * sn);
                _b1 = -2 * _gainAbs * ((_gainAbs - 1) + (_gainAbs + 1) * cs);
                _b2 = _gainAbs * ((_gainAbs + 1) + (_gainAbs - 1) * cs - beta * sn);
                _a0 = (_gainAbs + 1) - (_gainAbs - 1) * cs + beta * sn;
                _a1 = 2 * ((_gainAbs - 1) - (_gainAbs + 1) * cs);
                _a2 = (_gainAbs + 1) - (_gainAbs - 1) * cs - beta * sn;
                break;
        }
        _b0 /= _a0; _b1 /= _a0; _b2 /= _a0;
        _a1 /= _a0; _a2 /= _a0;
    }

    public double Filter(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }
}
