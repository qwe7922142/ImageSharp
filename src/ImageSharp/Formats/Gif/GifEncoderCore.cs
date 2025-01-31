// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using SixLabors.Memory;

namespace SixLabors.ImageSharp.Formats.Gif
{
    /// <summary>
    /// Implements the GIF encoding protocol.
    /// </summary>
    internal sealed class GifEncoderCore
    {
        /// <summary>
        /// Used for allocating memory during processing operations.
        /// </summary>
        private readonly MemoryAllocator memoryAllocator;

        /// <summary>
        /// Configuration bound to the encoding operation.
        /// </summary>
        private Configuration configuration;

        /// <summary>
        /// A reusable buffer used to reduce allocations.
        /// </summary>
        private readonly byte[] buffer = new byte[20];

        /// <summary>
        /// The quantizer used to generate the color palette.
        /// </summary>
        private readonly IQuantizer quantizer;

        /// <summary>
        /// The color table mode: Global or local.
        /// </summary>
        private GifColorTableMode? colorTableMode;

        /// <summary>
        /// The number of bits requires to store the color palette.
        /// </summary>
        private int bitDepth;

        /// <summary>
        /// Initializes a new instance of the <see cref="GifEncoderCore"/> class.
        /// </summary>
        /// <param name="memoryAllocator">The <see cref="MemoryAllocator"/> to use for buffer allocations.</param>
        /// <param name="options">The options for the encoder.</param>
        public GifEncoderCore(MemoryAllocator memoryAllocator, IGifEncoderOptions options)
        {
            this.memoryAllocator = memoryAllocator;
            this.quantizer = options.Quantizer;
            this.colorTableMode = options.ColorTableMode;
        }

        /// <summary>
        /// Encodes the image to the specified stream from the <see cref="Image{TPixel}"/>.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The <see cref="Image{TPixel}"/> to encode from.</param>
        /// <param name="stream">The <see cref="Stream"/> to encode the image data to.</param>
        public void Encode<TPixel>(Image<TPixel> image, Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            Guard.NotNull(image, nameof(image));
            Guard.NotNull(stream, nameof(stream));

            this.configuration = image.GetConfiguration();

            ImageMetadata metadata = image.Metadata;
            GifMetadata gifMetadata = metadata.GetFormatMetadata(GifFormat.Instance);
            this.colorTableMode = this.colorTableMode ?? gifMetadata.ColorTableMode;
            bool useGlobalTable = this.colorTableMode == GifColorTableMode.Global;

            // Quantize the image returning a palette.
            IQuantizedFrame<TPixel> quantized;
            using (IFrameQuantizer<TPixel> frameQuantizer = this.quantizer.CreateFrameQuantizer<TPixel>(image.GetConfiguration()))
            {
                quantized = frameQuantizer.QuantizeFrame(image.Frames.RootFrame);
            }

            // Get the number of bits.
            this.bitDepth = ImageMaths.GetBitsNeededForColorDepth(quantized.Palette.Length).Clamp(1, 8);

            // Write the header.
            this.WriteHeader(stream);

            // Write the LSD.
            int index = this.GetTransparentIndex(quantized);
            this.WriteLogicalScreenDescriptor(metadata, image.Width, image.Height, index, useGlobalTable, stream);

            if (useGlobalTable)
            {
                this.WriteColorTable(quantized, stream);
            }

            // Write the comments.
            this.WriteComments(gifMetadata, stream);

            // Write application extension to allow additional frames.
            if (image.Frames.Count > 1)
            {
                this.WriteApplicationExtension(stream, gifMetadata.RepeatCount);
            }

            if (useGlobalTable)
            {
                this.EncodeGlobal(image, quantized, index, stream);
            }
            else
            {
                this.EncodeLocal(image, quantized, stream);
            }

            // Clean up.
            quantized?.Dispose();

            // TODO: Write extension etc
            stream.WriteByte(GifConstants.EndIntroducer);
        }

        private void EncodeGlobal<TPixel>(Image<TPixel> image, IQuantizedFrame<TPixel> quantized, int transparencyIndex, Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            for (int i = 0; i < image.Frames.Count; i++)
            {
                ImageFrame<TPixel> frame = image.Frames[i];
                ImageFrameMetadata metadata = frame.Metadata;
                GifFrameMetadata frameMetadata = metadata.GetFormatMetadata(GifFormat.Instance);
                this.WriteGraphicalControlExtension(frameMetadata, transparencyIndex, stream);
                this.WriteImageDescriptor(frame, false, stream);

                if (i == 0)
                {
                    this.WriteImageData(quantized, stream);
                }
                else
                {
                    using (IFrameQuantizer<TPixel> paletteFrameQuantizer =
                        new PaletteFrameQuantizer<TPixel>(this.quantizer.Diffuser, quantized.Palette))
                    {
                        using (IQuantizedFrame<TPixel> paletteQuantized = paletteFrameQuantizer.QuantizeFrame(frame))
                        {
                            this.WriteImageData(paletteQuantized, stream);
                        }
                    }
                }
            }
        }

        private void EncodeLocal<TPixel>(Image<TPixel> image, IQuantizedFrame<TPixel> quantized, Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            ImageFrame<TPixel> previousFrame = null;
            GifFrameMetadata previousMeta = null;
            foreach (ImageFrame<TPixel> frame in image.Frames)
            {
                ImageFrameMetadata metadata = frame.Metadata;
                GifFrameMetadata frameMetadata = metadata.GetFormatMetadata(GifFormat.Instance);
                if (quantized is null)
                {
                    // Allow each frame to be encoded at whatever color depth the frame designates if set.
                    if (previousFrame != null && previousMeta.ColorTableLength != frameMetadata.ColorTableLength
                                              && frameMetadata.ColorTableLength > 0)
                    {
                        using (IFrameQuantizer<TPixel> frameQuantizer = this.quantizer.CreateFrameQuantizer<TPixel>(image.GetConfiguration(), frameMetadata.ColorTableLength))
                        {
                            quantized = frameQuantizer.QuantizeFrame(frame);
                        }
                    }
                    else
                    {
                        using (IFrameQuantizer<TPixel> frameQuantizer = this.quantizer.CreateFrameQuantizer<TPixel>(image.GetConfiguration()))
                        {
                            quantized = frameQuantizer.QuantizeFrame(frame);
                        }
                    }
                }

                this.bitDepth = ImageMaths.GetBitsNeededForColorDepth(quantized.Palette.Length).Clamp(1, 8);
                this.WriteGraphicalControlExtension(frameMetadata, this.GetTransparentIndex(quantized), stream);
                this.WriteImageDescriptor(frame, true, stream);
                this.WriteColorTable(quantized, stream);
                this.WriteImageData(quantized, stream);

                quantized?.Dispose();
                quantized = null; // So next frame can regenerate it
                previousFrame = frame;
                previousMeta = frameMetadata;
            }
        }

        /// <summary>
        /// Returns the index of the most transparent color in the palette.
        /// </summary>
        /// <param name="quantized">
        /// The quantized.
        /// </param>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <returns>
        /// The <see cref="int"/>.
        /// </returns>
        private int GetTransparentIndex<TPixel>(IQuantizedFrame<TPixel> quantized)
            where TPixel : struct, IPixel<TPixel>
        {
            // Transparent pixels are much more likely to be found at the end of a palette
            int index = -1;
            int length = quantized.Palette.Length;

            using (IMemoryOwner<Rgba32> rgbaBuffer = this.memoryAllocator.Allocate<Rgba32>(length))
            {
                Span<Rgba32> rgbaSpan = rgbaBuffer.GetSpan();
                ref Rgba32 paletteRef = ref MemoryMarshal.GetReference(rgbaSpan);
                PixelOperations<TPixel>.Instance.ToRgba32(this.configuration, quantized.Palette.Span, rgbaSpan);

                for (int i = quantized.Palette.Length - 1; i >= 0; i--)
                {
                    if (Unsafe.Add(ref paletteRef, i).Equals(default))
                    {
                        index = i;
                    }
                }
            }

            return index;
        }

        /// <summary>
        /// Writes the file header signature and version to the stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteHeader(Stream stream) => stream.Write(GifConstants.MagicNumber, 0, GifConstants.MagicNumber.Length);

        /// <summary>
        /// Writes the logical screen descriptor to the stream.
        /// </summary>
        /// <param name="metadata">The image metadata.</param>
        /// <param name="width">The image width.</param>
        /// <param name="height">The image height.</param>
        /// <param name="transparencyIndex">The transparency index to set the default background index to.</param>
        /// <param name="useGlobalTable">Whether to use a global or local color table.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteLogicalScreenDescriptor(
            ImageMetadata metadata,
            int width,
            int height,
            int transparencyIndex,
            bool useGlobalTable,
            Stream stream)
        {
            byte packedValue = GifLogicalScreenDescriptor.GetPackedValue(useGlobalTable, this.bitDepth - 1, false, this.bitDepth - 1);

            // The Pixel Aspect Ratio is defined to be the quotient of the pixel's
            // width over its height.  The value range in this field allows
            // specification of the widest pixel of 4:1 to the tallest pixel of
            // 1:4 in increments of 1/64th.
            //
            // Values :        0 -   No aspect ratio information is given.
            //            1..255 -   Value used in the computation.
            //
            // Aspect Ratio = (Pixel Aspect Ratio + 15) / 64
            byte ratio = 0;

            if (metadata.ResolutionUnits == PixelResolutionUnit.AspectRatio)
            {
                double hr = metadata.HorizontalResolution;
                double vr = metadata.VerticalResolution;
                if (hr != vr)
                {
                    if (hr > vr)
                    {
                        ratio = (byte)((hr * 64) - 15);
                    }
                    else
                    {
                        ratio = (byte)(((1 / vr) * 64) - 15);
                    }
                }
            }

            var descriptor = new GifLogicalScreenDescriptor(
                width: (ushort)width,
                height: (ushort)height,
                packed: packedValue,
                backgroundColorIndex: unchecked((byte)transparencyIndex),
                ratio);

            descriptor.WriteTo(this.buffer);

            stream.Write(this.buffer, 0, GifLogicalScreenDescriptor.Size);
        }

        /// <summary>
        /// Writes the application extension to the stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <param name="repeatCount">The animated image repeat count.</param>
        private void WriteApplicationExtension(Stream stream, ushort repeatCount)
        {
            // Application Extension Header
            if (repeatCount != 1)
            {
                var loopingExtension = new GifNetscapeLoopingApplicationExtension(repeatCount);
                this.WriteExtension(loopingExtension, stream);
            }
        }

        /// <summary>
        /// Writes the image comments to the stream.
        /// </summary>
        /// <param name="metadata">The metadata to be extract the comment data.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteComments(GifMetadata metadata, Stream stream)
        {
            if (metadata.Comments.Count == 0)
            {
                return;
            }

            foreach (string comment in metadata.Comments)
            {
                this.buffer[0] = GifConstants.ExtensionIntroducer;
                this.buffer[1] = GifConstants.CommentLabel;
                stream.Write(this.buffer, 0, 2);

                // Comment will be stored in chunks of 255 bytes, if it exceeds this size.
                ReadOnlySpan<char> commentSpan = comment.AsSpan();
                int idx = 0;
                for (; idx <= comment.Length - GifConstants.MaxCommentSubBlockLength; idx += GifConstants.MaxCommentSubBlockLength)
                {
                    WriteCommentSubBlock(stream, commentSpan, idx, GifConstants.MaxCommentSubBlockLength);
                }

                // Write the length bytes, if any, to another sub block.
                if (idx < comment.Length)
                {
                    int remaining = comment.Length - idx;
                    WriteCommentSubBlock(stream, commentSpan, idx, remaining);
                }

                stream.WriteByte(GifConstants.Terminator);
            }
        }

        /// <summary>
        /// Writes a comment sub-block to the stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <param name="commentSpan">Comment as a Span.</param>
        /// <param name="idx">Current start index.</param>
        /// <param name="length">The length of the string to write. Should not exceed 255 bytes.</param>
        private static void WriteCommentSubBlock(Stream stream, ReadOnlySpan<char> commentSpan, int idx, int length)
        {
            string subComment = commentSpan.Slice(idx, length).ToString();
            byte[] subCommentBytes = GifConstants.Encoding.GetBytes(subComment);
            stream.WriteByte((byte)length);
            stream.Write(subCommentBytes, 0, length);
        }

        /// <summary>
        /// Writes the graphics control extension to the stream.
        /// </summary>
        /// <param name="metadata">The metadata of the image or frame.</param>
        /// <param name="transparencyIndex">The index of the color in the color palette to make transparent.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteGraphicalControlExtension(GifFrameMetadata metadata, int transparencyIndex, Stream stream)
        {
            byte packedValue = GifGraphicControlExtension.GetPackedValue(
                disposalMethod: metadata.DisposalMethod,
                transparencyFlag: transparencyIndex > -1);

            var extension = new GifGraphicControlExtension(
                packed: packedValue,
                delayTime: (ushort)metadata.FrameDelay,
                transparencyIndex: unchecked((byte)transparencyIndex));

            this.WriteExtension(extension, stream);
        }

        /// <summary>
        /// Writes the provided extension to the stream.
        /// </summary>
        /// <param name="extension">The extension to write to the stream.</param>
        /// <param name="stream">The stream to write to.</param>
        public void WriteExtension(IGifExtension extension, Stream stream)
        {
            this.buffer[0] = GifConstants.ExtensionIntroducer;
            this.buffer[1] = extension.Label;

            int extensionSize = extension.WriteTo(this.buffer.AsSpan(2));

            this.buffer[extensionSize + 2] = GifConstants.Terminator;

            stream.Write(this.buffer, 0, extensionSize + 3);
        }

        /// <summary>
        /// Writes the image descriptor to the stream.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The <see cref="ImageFrame{TPixel}"/> to be encoded.</param>
        /// <param name="hasColorTable">Whether to use the global color table.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteImageDescriptor<TPixel>(ImageFrame<TPixel> image, bool hasColorTable, Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            byte packedValue = GifImageDescriptor.GetPackedValue(
                localColorTableFlag: hasColorTable,
                interfaceFlag: false,
                sortFlag: false,
                localColorTableSize: this.bitDepth - 1);

            var descriptor = new GifImageDescriptor(
                left: 0,
                top: 0,
                width: (ushort)image.Width,
                height: (ushort)image.Height,
                packed: packedValue);

            descriptor.WriteTo(this.buffer);

            stream.Write(this.buffer, 0, GifImageDescriptor.Size);
        }

        /// <summary>
        /// Writes the color table to the stream.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The <see cref="ImageFrame{TPixel}"/> to encode.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteColorTable<TPixel>(IQuantizedFrame<TPixel> image, Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            // The maximum number of colors for the bit depth
            int colorTableLength = ImageMaths.GetColorCountForBitDepth(this.bitDepth) * 3;
            int pixelCount = image.Palette.Length;

            using (IManagedByteBuffer colorTable = this.memoryAllocator.AllocateManagedByteBuffer(colorTableLength))
            {
                PixelOperations<TPixel>.Instance.ToRgb24Bytes(
                    this.configuration,
                    image.Palette.Span,
                    colorTable.GetSpan(),
                    pixelCount);
                stream.Write(colorTable.Array, 0, colorTableLength);
            }
        }

        /// <summary>
        /// Writes the image pixel data to the stream.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The <see cref="IQuantizedFrame{TPixel}"/> containing indexed pixels.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteImageData<TPixel>(IQuantizedFrame<TPixel> image, Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            using (var encoder = new LzwEncoder(this.memoryAllocator, (byte)this.bitDepth))
            {
                encoder.Encode(image.GetPixelSpan(), stream);
            }
        }
    }
}
