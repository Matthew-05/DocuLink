export function normalizeRotation(rotation: number): number {
  return ((rotation % 360) + 360) % 360;
}

export function resolvePageRotation(nativeRotation: number, storedRotation: number): number {
  return normalizeRotation(nativeRotation + storedRotation);
}
