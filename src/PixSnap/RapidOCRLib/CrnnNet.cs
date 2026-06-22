using Emgu.CV;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RapidOCRLib.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RapidOCRLib
{
    class CrnnNet : IDisposable
    {
        private readonly float[] MeanValues = { 127.5F, 127.5F, 127.5F };
        private readonly float[] NormValues = { 1.0F / 127.5F, 1.0F / 127.5F, 1.0F / 127.5F };
        private const int crnnDstHeight = 48;
        private const int crnnCols = 6625;

        private InferenceSession crnnNet = null!;
        private List<string> keys = null!;
        private List<string> inputNames = null!;

        public CrnnNet() { }

        ~CrnnNet()
        {
            crnnNet?.Dispose();
        }

        public void Dispose()
        {
            crnnNet?.Dispose();
            crnnNet = null!;
            GC.SuppressFinalize(this);
        }

        public async Task InitModel(string path, string keysPath, int numThread)
        {
            try
            {
                SessionOptions op = new SessionOptions();
                op.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED;
                op.InterOpNumThreads = numThread;
                op.IntraOpNumThreads = numThread;
                crnnNet = new InferenceSession(path, op);
                inputNames = crnnNet.InputMetadata.Keys.ToList();
                keys = InitKeys(keysPath);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message + ex.StackTrace);
                throw;
            }
        }
        private List<string> InitKeys(string path)
        {
            using StreamReader sr = new StreamReader(path, Encoding.UTF8);
            List<string> keys = new List<string>();
            keys.Add("#");
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                keys.Add(line);
            }
            keys.Add(" ");
            Console.WriteLine($"keys Size = {keys.Count}");
            return keys;
        }

        public List<TextLine> GetTextLines(List<Mat> partImgs)
        {
            List<TextLine> textLines = new List<TextLine>();
            for (int i = 0; i < partImgs.Count; i++)
            {
                var startTicks = DateTime.Now.Ticks;
                var textLine = GetTextLine(partImgs[i]);
                var endTicks = DateTime.Now.Ticks;
                var crnnTime = (endTicks - startTicks) / 10000F;
                textLine.Time = crnnTime;
                textLines.Add(textLine);
            }
            return textLines;
        }

        private TextLine GetTextLine(Mat src)
        {
            TextLine textLine = new TextLine();
            if (src == null || src.IsEmpty || src.Rows < 1 || src.Cols < 1)
                return textLine;

            float scale = (float)crnnDstHeight / (float)src.Rows;
            int dstWidth = Math.Max(1, (int)((float)src.Cols * scale));

            using Mat srcResize = new Mat();
            CvInvoke.Resize(src, srcResize, new Size(dstWidth, crnnDstHeight));
            Tensor<float> inputTensors = OcrUtils.SubstractMeanNormalize(srcResize, MeanValues, NormValues);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputNames[0], inputTensors)
            };
            try
            {
                using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = crnnNet.Run(inputs))
                {
                    var resultsArray = results.ToArray();
                    var dimensions = resultsArray[0].AsTensor<float>().Dimensions;
                    float[] outputData = resultsArray[0].AsEnumerable<float>().ToArray();

                    return ScoreToTextLine(outputData, dimensions[1], dimensions[2]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message + ex.StackTrace);
                //throw ex;
            }

            return textLine;
        }

        private TextLine ScoreToTextLine(float[] srcData, int h, int w)
        {
            StringBuilder sb = new StringBuilder();
            TextLine textLine = new TextLine();

            int lastIndex = 0;
            List<float> scores = new List<float>();

            for (int i = 0; i < h; i++)
            {
                var (maxIndex, maxProb) = DecodeTimestep(srcData, i, w);

                if (maxIndex > 0 && maxIndex < keys.Count && (!(i > 0 && maxIndex == lastIndex)))
                {
                    scores.Add(maxProb);
                    sb.Append(keys[maxIndex]);
                }
                lastIndex = maxIndex;
            }
            textLine.Text = sb.ToString();
            textLine.CharScores = scores;
            return textLine;
        }

        /// <summary>
        /// 与 PaddleOCR 一致：对 CTC 每步输出取 argmax 概率；若已是 softmax 则直接使用。
        /// </summary>
        private static (int MaxIndex, float MaxProb) DecodeTimestep(float[] srcData, int row, int w)
        {
            int offset = row * w;
            float sum = 0f;
            for (int j = 0; j < w; j++)
                sum += srcData[offset + j];

            if (sum is >= 0.99f and <= 1.01f)
            {
                int maxIndex = 0;
                float maxProb = srcData[offset];
                for (int j = 1; j < w; j++)
                {
                    float v = srcData[offset + j];
                    if (v > maxProb)
                    {
                        maxProb = v;
                        maxIndex = j;
                    }
                }
                return (maxIndex, maxProb);
            }

            int bestIndex = 0;
            float maxLogit = srcData[offset];
            for (int j = 1; j < w; j++)
            {
                float v = srcData[offset + j];
                if (v > maxLogit)
                {
                    maxLogit = v;
                    bestIndex = j;
                }
            }

            float expSum = 0f;
            for (int j = 0; j < w; j++)
                expSum += MathF.Exp(srcData[offset + j] - maxLogit);

            float prob = expSum > 0f
                ? MathF.Exp(srcData[offset + bestIndex] - maxLogit) / expSum
                : 0f;
            return (bestIndex, prob);
        }

    }
}