/** Decoded text-geometry-v1 document (contracts/text-geometry-v1.json). */
export interface TextGeometry {
  version: 1;
  coordinateSpace: "normalized";
  pages: TextGeometryPage[];
}

export interface TextGeometryPage {
  pageIndex: number;
  characters: TextGeometryCharacter[];
}

export interface TextGeometryCharacter {
  char: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

/** Decompresses gzip-compressed text-geometry-v1 JSON from a base64 persistence blob. */
export async function decodeTextGeometry(base64: string): Promise<TextGeometry> {
  const compressed = base64ToBytes(base64);
  const stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream("gzip"));
  const json = await new Response(stream).text();
  return JSON.parse(json) as TextGeometry;
}
