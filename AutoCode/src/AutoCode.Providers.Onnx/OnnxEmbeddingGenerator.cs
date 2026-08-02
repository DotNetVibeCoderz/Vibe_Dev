// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AutoCode.Providers.Onnx;

/// <summary>
/// Embeddings from a local ONNX sentence-transformer — no server, no network, no API key.
///
/// EN: this is the cheapest way to have a semantic code index. Indexing a repository means embedding
/// every source file, and doing that through a paid API costs real money to produce a result that a
/// 90 MB local model matches. It is also the only option that works with no network at all, which
/// matters for code that cannot leave the machine.
/// ID: ini cara paling murah untuk memiliki indeks kode semantik. Mengindeks repositori berarti
/// meng-embed seluruh berkas sumber, dan melakukannya lewat API berbayar menghabiskan biaya nyata
/// untuk hasil yang setara dengan model lokal 90 MB. Ini juga satu-satunya pilihan yang bekerja
/// sepenuhnya tanpa jaringan — penting untuk kode yang tidak boleh keluar dari mesin.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly EmbeddingOptions _options;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private readonly string[] _inputNames;
    private readonly string _outputName;
    private readonly bool _outputIsPooled;

    private OnnxEmbeddingGenerator(
        InferenceSession session,
        BertTokenizer tokenizer,
        EmbeddingOptions options)
    {
        _session = session;
        _tokenizer = tokenizer;
        _options = options;

        // A model exported with a pooling head emits a 2D sentence embedding directly; a plain
        // encoder emits 3D token states that still have to be pooled here.
        var output = session.OutputMetadata.FirstOrDefault(o =>
            o.Key.Contains("sentence_embedding", StringComparison.OrdinalIgnoreCase));

        if (output.Key is null)
            output = session.OutputMetadata.First();

        _outputName = output.Key;
        _outputIsPooled = output.Value.Dimensions.Length == 2;

        _inputNames = [.. session.InputMetadata.Keys];

        _metadata = new EmbeddingGeneratorMetadata(
            "onnx",
            new Uri("file:///" + Path.GetFullPath(options.ModelPath!).Replace('\\', '/')),
            Path.GetFileNameWithoutExtension(options.ModelPath));
    }

    /// <summary>
    /// Loads a model and its vocabulary.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The paths are missing or the files do not exist. Failing here with a usable message beats
    /// failing later inside the runtime with one that is not.
    /// </exception>
    public static OnnxEmbeddingGenerator Create(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new InvalidOperationException("The ONNX embedding backend needs embeddings.modelPath.");

        if (string.IsNullOrWhiteSpace(options.VocabPath))
            throw new InvalidOperationException("The ONNX embedding backend needs embeddings.vocabPath (the model's vocab.txt).");

        if (!File.Exists(options.ModelPath))
            throw new InvalidOperationException($"ONNX model not found: {options.ModelPath}");

        if (!File.Exists(options.VocabPath))
            throw new InvalidOperationException($"Vocabulary not found: {options.VocabPath}");

        var sessionOptions = new SessionOptions
        {
            // Indexing runs alongside the agent; leaving a core free keeps the terminal responsive.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        var session = new InferenceSession(options.ModelPath, sessionOptions);

        var tokenizer = BertTokenizer.Create(
            options.VocabPath,
            new BertOptions { LowerCaseBeforeTokenization = options.LowerCase });

        return new OnnxEmbeddingGenerator(session, tokenizer, options);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
            return null;

        return serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
             : serviceType.IsInstanceOfType(this) ? this
             : null;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var texts = values as IList<string> ?? [.. values];

        if (texts.Count == 0)
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>());

        // ONNX Runtime is synchronous and CPU-bound. Running it on the thread pool keeps the
        // agent loop's cancellation and streaming responsive during a long index build.
        return Task.Run(() => Generate(texts, cancellationToken), cancellationToken);
    }

    private GeneratedEmbeddings<Embedding<float>> Generate(IList<string> texts, CancellationToken cancellationToken)
    {
        var encoded = new List<long[]>(texts.Count);
        var longest = 1;

        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = _tokenizer.EncodeToIds(
                text,
                maxTokenCount: _options.MaxTokens,
                out _,
                out _);

            var tokens = ids.Count == 0 ? [0L] : ids.Select(id => (long)id).ToArray();
            encoded.Add(tokens);
            longest = Math.Max(longest, tokens.Length);
        }

        var batch = encoded.Count;
        var inputIds = new DenseTensor<long>([batch, longest]);
        var attentionMask = new DenseTensor<long>([batch, longest]);
        var tokenTypeIds = new DenseTensor<long>([batch, longest]);

        for (var row = 0; row < batch; row++)
        {
            var tokens = encoded[row];

            for (var column = 0; column < tokens.Length; column++)
            {
                inputIds[row, column] = tokens[column];
                attentionMask[row, column] = 1;
            }

            // Remaining positions stay zero: pad token, masked out, segment 0.
        }

        var inputs = new List<NamedOnnxValue>(_inputNames.Length);

        foreach (var name in _inputNames)
        {
            inputs.Add(name switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(name, inputIds),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(name, attentionMask),
                "token_type_ids" => NamedOnnxValue.CreateFromTensor(name, tokenTypeIds),
                _ => throw new InvalidOperationException(
                    $"The ONNX model expects an input named '{name}', which Auto Code does not know how to " +
                    "supply. Export the model with the standard BERT inputs (input_ids, attention_mask, " +
                    "token_type_ids)."),
            });
        }

        using var results = _session.Run(inputs, [_outputName]);
        var tensor = results.First().AsTensor<float>();

        var embeddings = new GeneratedEmbeddings<Embedding<float>>(batch);

        for (var row = 0; row < batch; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vector = _outputIsPooled
                ? ReadPooledRow(tensor, row)
                : MeanPool(tensor, attentionMask, row);

            Normalize(vector);
            embeddings.Add(new Embedding<float>(vector));
        }

        return embeddings;
    }

    private static float[] ReadPooledRow(Tensor<float> tensor, int row)
    {
        var width = tensor.Dimensions[1];
        var vector = new float[width];

        for (var i = 0; i < width; i++)
            vector[i] = tensor[row, i];

        return vector;
    }

    /// <summary>
    /// Averages the token states, ignoring padding.
    ///
    /// EN: mean pooling over the attention mask is what sentence-transformer models are trained
    /// against. Taking the [CLS] token instead — a common shortcut — produces vectors that cluster
    /// far more weakly, and the resulting index quietly returns poor matches rather than failing.
    /// ID: mean pooling dengan attention mask adalah cara model sentence-transformer dilatih.
    /// Memakai token [CLS] sebagai jalan pintas menghasilkan vektor yang jauh lebih lemah, dan
    /// indeksnya diam-diam mengembalikan hasil buruk alih-alih gagal terang-terangan.
    /// </summary>
    private static float[] MeanPool(Tensor<float> hidden, DenseTensor<long> attentionMask, int row)
    {
        var sequenceLength = hidden.Dimensions[1];
        var width = hidden.Dimensions[2];
        var vector = new float[width];
        var counted = 0;

        for (var position = 0; position < sequenceLength; position++)
        {
            if (attentionMask[row, position] == 0)
                continue;

            counted++;

            for (var i = 0; i < width; i++)
                vector[i] += hidden[row, position, i];
        }

        if (counted > 1)
        {
            for (var i = 0; i < width; i++)
                vector[i] /= counted;
        }

        return vector;
    }

    /// <summary>
    /// L2-normalises in place, so cosine similarity reduces to a dot product and vectors from
    /// different-length inputs stay comparable.
    /// </summary>
    private static void Normalize(float[] vector)
    {
        var sumOfSquares = 0.0;

        foreach (var value in vector)
            sumOfSquares += value * value;

        var magnitude = Math.Sqrt(sumOfSquares);

        if (magnitude < 1e-9)
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / magnitude);
    }

    public void Dispose() => _session.Dispose();
}
