namespace RemoteControl.Net.Fec;

/// <summary>Small dense matrix over GF(256), just big enough to support ReedSolomonCodec's matrix construction/inversion. Not optimized -- these operations run once per FEC block setup, not per byte.</summary>
public sealed class GF256Matrix
{
    private readonly byte[,] _values;

    public int Rows { get; }
    public int Cols { get; }

    public GF256Matrix(int rows, int cols)
    {
        Rows = rows;
        Cols = cols;
        _values = new byte[rows, cols];
    }

    public byte this[int row, int col]
    {
        get => _values[row, col];
        set => _values[row, col] = value;
    }

    public static GF256Matrix Identity(int size)
    {
        var m = new GF256Matrix(size, size);
        for (var i = 0; i < size; i++) m[i, i] = 1;
        return m;
    }

    /// <summary>Vandermonde matrix with rows evaluated at x = 0, 1, ..., rows-1 and columns 0..cols-1 (V[r][c] = r^c, with 0^0 = 1 by convention).</summary>
    public static GF256Matrix Vandermonde(int rows, int cols)
    {
        if (rows > 256) throw new ArgumentOutOfRangeException(nameof(rows), "GF(256) only has 256 distinct evaluation points.");
        var m = new GF256Matrix(rows, cols);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                m[r, c] = GF256.Pow((byte)r, c);
            }
        }
        return m;
    }

    public GF256Matrix Multiply(GF256Matrix other)
    {
        if (Cols != other.Rows) throw new InvalidOperationException("Matrix dimension mismatch.");
        var result = new GF256Matrix(Rows, other.Cols);
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < other.Cols; c++)
            {
                byte sum = 0;
                for (var k = 0; k < Cols; k++)
                {
                    sum ^= GF256.Multiply(this[r, k], other[k, c]);
                }
                result[r, c] = sum;
            }
        }
        return result;
    }

    public GF256Matrix SubMatrix(int rowStart, int rowCount, int colStart, int colCount)
    {
        var result = new GF256Matrix(rowCount, colCount);
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < colCount; c++)
            {
                result[r, c] = this[rowStart + r, colStart + c];
            }
        }
        return result;
    }

    /// <summary>Selects an arbitrary subset of rows (by index), in the given order -- used to pick out the rows corresponding to whichever K shards were actually received.</summary>
    public GF256Matrix SelectRows(IReadOnlyList<int> rowIndices)
    {
        var result = new GF256Matrix(rowIndices.Count, Cols);
        for (var r = 0; r < rowIndices.Count; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                result[r, c] = this[rowIndices[r], c];
            }
        }
        return result;
    }

    /// <summary>Gauss-Jordan inversion over GF(256). Throws if the matrix is singular (callers only ever invert submatrices that are mathematically guaranteed invertible -- see ReedSolomonCodec's Vandermonde-MDS argument -- so this should never actually throw in practice; it's a correctness backstop, not expected user-facing behavior).</summary>
    public GF256Matrix Invert()
    {
        if (Rows != Cols) throw new InvalidOperationException("Only square matrices can be inverted.");
        var size = Rows;

        // Augmented [this | identity], reduced in place; the right half ends up as the inverse.
        var left = new byte[size, size];
        var right = new byte[size, size];
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++) left[r, c] = this[r, c];
            right[r, r] = 1;
        }

        for (var pivotRow = 0; pivotRow < size; pivotRow++)
        {
            if (left[pivotRow, pivotRow] == 0)
            {
                var swapWith = -1;
                for (var r = pivotRow + 1; r < size; r++)
                {
                    if (left[r, pivotRow] != 0) { swapWith = r; break; }
                }
                if (swapWith < 0) throw new InvalidOperationException("Matrix is singular and cannot be inverted.");
                SwapRows(left, pivotRow, swapWith, size);
                SwapRows(right, pivotRow, swapWith, size);
            }

            var inversePivot = GF256.Inverse(left[pivotRow, pivotRow]);
            ScaleRow(left, pivotRow, inversePivot, size);
            ScaleRow(right, pivotRow, inversePivot, size);

            for (var r = 0; r < size; r++)
            {
                if (r == pivotRow) continue;
                var factor = left[r, pivotRow];
                if (factor == 0) continue;
                EliminateRow(left, r, pivotRow, factor, size);
                EliminateRow(right, r, pivotRow, factor, size);
            }
        }

        var inverse = new GF256Matrix(size, size);
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                inverse[r, c] = right[r, c];
        return inverse;
    }

    private static void SwapRows(byte[,] m, int a, int b, int size)
    {
        for (var c = 0; c < size; c++) (m[a, c], m[b, c]) = (m[b, c], m[a, c]);
    }

    private static void ScaleRow(byte[,] m, int row, byte factor, int size)
    {
        for (var c = 0; c < size; c++) m[row, c] = GF256.Multiply(m[row, c], factor);
    }

    private static void EliminateRow(byte[,] m, int targetRow, int pivotRow, byte factor, int size)
    {
        for (var c = 0; c < size; c++) m[targetRow, c] ^= GF256.Multiply(m[pivotRow, c], factor);
    }
}
