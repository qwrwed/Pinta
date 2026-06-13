//
// PdnFormat.cs
//
// Author:
//       Pinta contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Formats.Nrbf;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Cairo;
using GdkPixbuf;

// System.Formats.Nrbf is shipped as an evaluation-only ("experimental") API surface, but it is
// the framework's supported way to read .NET Binary Format payloads. Opt in to it here.
#pragma warning disable SYSLIB5005

namespace Pinta.Core;

/// <summary>
/// Importer for Paint.NET's native ".pdn" format.
///
/// A .pdn file is a small header followed by a .NET Binary Format (NRBF) serialization
/// of Paint.NET's Document object graph, followed by the raw layer pixel data which is
/// written separately (deferred) after the graph, one block per layer in layer order.
///
/// We use the framework's <see cref="NrbfDecoder"/> to parse the object graph (it stops
/// reading exactly at the end of the serialized payload, leaving the stream positioned at
/// the start of the deferred pixel data), then read each layer's pixel block by hand. The
/// format of the deferred MemoryBlock data is described here:
/// https://github.com/rivy/OpenPDN/blob/master/src/Core/MemoryBlock.cs
///
/// Only importing is supported; Paint.NET's format relies on .NET binary serialization that
/// would be fragile to write back out, and ".ora" already covers layered interchange.
/// </summary>
public sealed class PdnFormat : IImageImporter
{
	public Document Import (Gio.File file)
	{
		// Read the whole file into a seekable buffer. The NRBF decoder and the
		// hand-rolled pixel reader both consume the same stream sequentially.
		using MemoryStream buffer = new ();
		using (GioStream input = new (file.Read (cancellable: null)))
			input.CopyTo (buffer);
		buffer.Position = 0;

		Stream payload = OpenPayloadStream (buffer);

		ClassRecord root = (NrbfDecoder.Decode (payload, options: null, leaveOpen: true) as ClassRecord)
			?? throw new FormatException ("Unexpected root record in .pdn file.");

		int width = root.GetInt32 (FindMember (root, "width"));
		int height = root.GetInt32 (FindMember (root, "height"));

		Size imageSize = new (width, height);

		Document document = new (
			PintaCore.Actions,
			PintaCore.Tools,
			PintaCore.Workspace,
			imageSize,
			file,
			"pdn");

		ClassRecord layerList = root.GetClassRecord (FindMember (root, "layers"))
			?? throw new FormatException ("Missing layer list in .pdn file.");

		int layerCount = layerList.GetInt32 (FindMember (layerList, "_size"));
		var items = ((SZArrayRecord<SerializationRecord?>) layerList.GetArrayRecord (FindMember (layerList, "_items"))!)
			.GetArray ();

		// Paint.NET stores layers bottom-first, which matches Pinta's index order,
		// so each layer is appended on top of the previous one.
		for (int i = 0; i < layerCount; i++) {

			if (items[i] is not ClassRecord bitmapLayer)
				throw new FormatException ($"Layer {i} is missing in .pdn file.");

			ReadLayer (document, bitmapLayer, imageSize, payload);
		}

		return document;
	}

	/// <summary>
	/// Validates the .pdn header and returns the stream positioned at the start of the
	/// serialized document. If the document container is gzip-compressed, the remainder is
	/// transparently decompressed into a new buffer.
	/// </summary>
	private static Stream OpenPayloadStream (MemoryStream buffer)
	{
		Span<byte> magic = stackalloc byte[4];
		buffer.ReadExactly (magic);
		if (!magic.SequenceEqual ("PDN3"u8))
			throw new FormatException ("Not a Paint.NET 3+ (.pdn) file. Older .pdn files are not supported.");

		// 24-bit little-endian length of the XML metadata header, which we skip.
		Span<byte> headerSizeBytes = stackalloc byte[4];
		buffer.ReadExactly (headerSizeBytes[..3]);
		headerSizeBytes[3] = 0;
		int headerSize = BinaryPrimitives.ReadInt32LittleEndian (headerSizeBytes);
		buffer.Position += headerSize;

		// Indicator for the serialized document: 0x00 0x01 = uncompressed, 0x1F 0x8B = gzip.
		long indicatorPos = buffer.Position;
		Span<byte> indicator = stackalloc byte[2];
		buffer.ReadExactly (indicator);

		if (indicator[0] == 0x00 && indicator[1] == 0x01)
			return buffer;

		if (indicator[0] == 0x1F && indicator[1] == 0x8B) {
			// The whole document (graph + deferred pixel data) is gzip-compressed.
			buffer.Position = indicatorPos;
			MemoryStream decompressed = new ();
			using (GZipStream gz = new (buffer, CompressionMode.Decompress, leaveOpen: true))
				gz.CopyTo (decompressed);
			decompressed.Position = 0;
			return decompressed;
		}

		throw new FormatException ("Invalid data indicator in .pdn file. File may be corrupted.");
	}

	private static void ReadLayer (Document document, ClassRecord bitmapLayer, Size imageSize, Stream payload)
	{
		ClassRecord properties = bitmapLayer.GetClassRecord (FindMember (bitmapLayer, "Layer+properties"))
			?? throw new FormatException ("Missing layer properties in .pdn file.");

		string? name = properties.GetString (FindMember (properties, "name"));
		string displayName = name ?? "(unnamed)";
		bool visible = GetBoolean (properties, "visible", true);
		int opacity = GetInt32 (properties, "opacity", 255);

		ClassRecord surface = bitmapLayer.GetClassRecord (FindMember (bitmapLayer, "surface"))
			?? throw new FormatException ($"Missing surface for layer \"{displayName}\" in .pdn file.");
		int stride = surface.GetInt32 (FindMember (surface, "stride"));

		ClassRecord scan0 = surface.GetClassRecord (FindMember (surface, "scan0"))
			?? throw new FormatException ($"Missing pixel data for layer \"{displayName}\" in .pdn file.");
		long length64 = scan0.GetInt64 (FindMember (scan0, "length64"));

		if (length64 <= 0 || length64 > int.MaxValue)
			throw new FormatException ($"Unsupported pixel data length for layer \"{displayName}\" in .pdn file.");

		byte[] data = ReadMemoryBlock (payload, (int) length64);

		int bpp = stride * 8 / imageSize.Width;
		if (bpp != 32 && bpp != 24)
			throw new FormatException ($"Unsupported bit depth ({bpp}) for layer \"{displayName}\" in .pdn file.");

		bool hasAlpha = bpp == 32;
		int channels = bpp / 8;

		// Paint.NET stores pixels as BGR(A); Gdk expects RGB(A), so swap the blue and red channels.
		for (int p = 0; p + channels <= data.Length; p += channels)
			(data[p], data[p + 2]) = (data[p + 2], data[p]);

		UserLayer layer = document.Layers.CreateLayer (name);
		document.Layers.Insert (layer, document.Layers.Count ());

		layer.Hidden = !visible;
		layer.Opacity = opacity / 255.0;
		layer.BlendMode = ResolveBlendMode (bitmapLayer, properties);

		using GLib.Bytes bytes = GLib.Bytes.New (data);
		using Pixbuf pb = Pixbuf.NewFromBytes (bytes, Colorspace.Rgb, hasAlpha, 8, imageSize.Width, imageSize.Height, stride);
		using Context g = new (layer.Surface);
		g.DrawPixbuf (pb, PointD.Zero);
	}

	/// <summary>
	/// Reads a single deferred Paint.NET MemoryBlock from the stream into a byte array of the
	/// given length. The block is stored as a series of (possibly gzip-compressed) chunks.
	/// </summary>
	private static byte[] ReadMemoryBlock (Stream stream, int length)
	{
		byte[] data = new byte[length];

		int formatVersion = stream.ReadByte (); // 0 = gzip-compressed chunks, 1 = uncompressed.
		if (formatVersion < 0)
			throw new EndOfStreamException ("Truncated pixel data in .pdn file.");

		int chunkSize = (int) ReadUInt32BigEndian (stream);
		if (chunkSize <= 0)
			throw new FormatException ("Invalid chunk size in .pdn file.");

		int chunkCount = (length + chunkSize - 1) / chunkSize;
		bool[] chunksFound = new bool[chunkCount];

		for (int c = 0; c < chunkCount; c++) {

			int chunkNumber = (int) ReadUInt32BigEndian (stream);
			if (chunkNumber < 0 || chunkNumber >= chunkCount)
				throw new FormatException ("Chunk number out of bounds in .pdn file.");
			if (chunksFound[chunkNumber])
				throw new FormatException ("Duplicate chunk in .pdn file.");
			chunksFound[chunkNumber] = true;

			int dataSize = (int) ReadUInt32BigEndian (stream);
			int chunkOffset = chunkNumber * chunkSize;
			int actualChunkSize = Math.Min (chunkSize, length - chunkOffset);

			byte[] rawData = new byte[dataSize];
			stream.ReadExactly (rawData);

			if (formatVersion == 0) {
				using MemoryStream compressed = new (rawData);
				using GZipStream gz = new (compressed, CompressionMode.Decompress);
				gz.ReadExactly (data.AsSpan (chunkOffset, actualChunkSize));
			} else {
				Array.Copy (rawData, 0, data, chunkOffset, actualChunkSize);
			}
		}

		return data;
	}

	private static uint ReadUInt32BigEndian (Stream stream)
	{
		Span<byte> b = stackalloc byte[4];
		stream.ReadExactly (b);
		return BinaryPrimitives.ReadUInt32BigEndian (b);
	}

	/// <summary>
	/// Finds the serialized member whose name matches <paramref name="name"/>, accounting for
	/// the "DeclaringType+field" prefix that .NET serialization adds to inherited fields. An
	/// exact match wins; otherwise a member whose name ends with "+name" is accepted (only when
	/// the requested name is itself unqualified).
	/// </summary>
	private static string FindMember (ClassRecord record, string name)
	{
		foreach (string member in record.MemberNames)
			if (member == name)
				return member;

		if (!name.Contains ('+')) {
			foreach (string member in record.MemberNames)
				if (member.EndsWith ('+' + name, StringComparison.Ordinal))
					return member;
		}

		throw new FormatException ($"Missing member \"{name}\" in .pdn file.");
	}

	private static bool GetBoolean (ClassRecord record, string name, bool fallback)
	{
		string? member = TryFindMember (record, name);
		return member is null ? fallback : Convert.ToBoolean (record.GetRawValue (member));
	}

	private static int GetInt32 (ClassRecord record, string name, int fallback)
	{
		string? member = TryFindMember (record, name);
		object? value = member is null ? null : record.GetRawValue (member);
		return value is null ? fallback : Convert.ToInt32 (value);
	}

	private static string? TryFindMember (ClassRecord record, string name)
	{
		foreach (string member in record.MemberNames)
			if (member == name)
				return member;
		if (!name.Contains ('+')) {
			foreach (string member in record.MemberNames)
				if (member.EndsWith ('+' + name, StringComparison.Ordinal))
					return member;
		}
		return null;
	}

	private static BlendMode ResolveBlendMode (ClassRecord bitmapLayer, ClassRecord properties)
	{
		// Newer files store the blend mode as an enum on the layer properties.
		string? blendModeMember = TryFindMember (properties, "blendMode");
		if (blendModeMember is not null && properties.GetClassRecord (blendModeMember) is ClassRecord blendModeEnum) {
			string? valueMember = TryFindMember (blendModeEnum, "value__");
			if (valueMember is not null)
				return MapBlendType (Convert.ToInt32 (blendModeEnum.GetRawValue (valueMember)));
		}

		// Otherwise fall back to the blend operation class stored on the bitmap layer properties.
		string? bitmapPropsMember = TryFindMember (bitmapLayer, "properties");
		if (bitmapPropsMember is not null && bitmapLayer.GetClassRecord (bitmapPropsMember) is ClassRecord bitmapProps) {
			string? blendOpMember = TryFindMember (bitmapProps, "blendOp");
			if (blendOpMember is not null && bitmapProps.GetClassRecord (blendOpMember) is ClassRecord blendOp)
				return MapBlendOpName (blendOp.TypeName.FullName);
		}

		return BlendMode.Normal;
	}

	// Paint.NET's LayerBlendMode enum order. Modes that Pinta does not implement
	// (Additive, Reflect, Glow, Negation) fall back to Normal.
	private static BlendMode MapBlendType (int value) => value switch {
		1 => BlendMode.Multiply,
		3 => BlendMode.ColorBurn,
		4 => BlendMode.ColorDodge,
		7 => BlendMode.Overlay,
		8 => BlendMode.Difference,
		10 => BlendMode.Lighten,
		11 => BlendMode.Darken,
		12 => BlendMode.Screen,
		13 => BlendMode.Xor,
		_ => BlendMode.Normal,
	};

	private static BlendMode MapBlendOpName (string? blendOpTypeName)
	{
		if (string.IsNullOrEmpty (blendOpTypeName))
			return BlendMode.Normal;

		// e.g. "PaintDotNet.UserBlendOps+MultiplyBlendOp"
		string shortName = blendOpTypeName;
		int plus = shortName.LastIndexOf ('+');
		if (plus >= 0)
			shortName = shortName[(plus + 1)..];

		return shortName switch {
			"MultiplyBlendOp" => BlendMode.Multiply,
			"ColorBurnBlendOp" => BlendMode.ColorBurn,
			"ColorDodgeBlendOp" => BlendMode.ColorDodge,
			"OverlayBlendOp" => BlendMode.Overlay,
			"DifferenceBlendOp" => BlendMode.Difference,
			"LightenBlendOp" => BlendMode.Lighten,
			"DarkenBlendOp" => BlendMode.Darken,
			"ScreenBlendOp" => BlendMode.Screen,
			"XorBlendOp" => BlendMode.Xor,
			_ => BlendMode.Normal,
		};
	}
}
