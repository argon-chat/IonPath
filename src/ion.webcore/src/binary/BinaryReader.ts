export class BinaryReader {
  private view: DataView;
  private offset: number = 0;

  constructor(buffer: ArrayBuffer | Uint8Array) {
    if (buffer instanceof Uint8Array) {
      this.view = new DataView(
        buffer.buffer,
        buffer.byteOffset,
        buffer.byteLength
      );
    } else {
      this.view = new DataView(buffer);
    }
  }

  get position() {
    return this.offset;
  }

  seek(position: number) {
    this.offset = position;
  }

  skip(bytes: number) {
    this.offset += bytes;
  }

  readInt8(): number {
    return this.view.getInt8(this.offset++);
  }

  readUint8(): number {
    return this.view.getUint8(this.offset++);
  }

  readInt16(littleEndian = true): number {
    const val = this.view.getInt16(this.offset, littleEndian);
    this.offset += 2;
    return val;
  }

  readUint16(littleEndian = true): number {
    const val = this.view.getUint16(this.offset, littleEndian);
    this.offset += 2;
    return val;
  }

  readInt32(littleEndian = true): number {
    const val = this.view.getInt32(this.offset, littleEndian);
    this.offset += 4;
    return val;
  }

  readUint32(littleEndian = true): number {
    const val = this.view.getUint32(this.offset, littleEndian);
    this.offset += 4;
    return val;
  }

  readBigInt64(littleEndian = true): bigint {
    const val = this.view.getBigInt64(this.offset, littleEndian);
    this.offset += 8;
    return val;
  }

  readBigUint64(littleEndian = true): bigint {
    const val = this.view.getBigUint64(this.offset, littleEndian);
    this.offset += 8;
    return val;
  }

  readInt128(littleEndian = true): bigint {
    const bytes = this.readBytes(16);
    let result = 0n;
    if (littleEndian) {
      for (let i = 15; i >= 0; i--) {
        result = (result << 8n) | BigInt(bytes[i]);
      }
    } else {
      for (let i = 0; i < 16; i++) {
        result = (result << 8n) | BigInt(bytes[i]);
      }
    }
    if (bytes[littleEndian ? 15 : 0] & 0x80) {
      const mask = (1n << 128n) - 1n;
      result = -((~result & mask) + 1n);
    }
    return result;
  }

  readFloat32(littleEndian = true): number {
    const val = this.view.getFloat32(this.offset, littleEndian);
    this.offset += 4;
    return val;
  }

  readFloat64(littleEndian = true): number {
    const val = this.view.getFloat64(this.offset, littleEndian);
    this.offset += 8;
    return val;
  }

  readFloat16(littleEndian = true): number {
    const bits = this.view.getUint16(this.offset, littleEndian);
    this.offset += 2;

    const sign = (bits & 0x8000) >> 15;
    const exponent = (bits & 0x7c00) >> 10;
    const fraction = bits & 0x03ff;

    if (exponent === 0) {
      return (sign ? -1 : 1) * Math.pow(2, -14) * (fraction / Math.pow(2, 10));
    } else if (exponent === 0x1f) {
      return fraction === 0 ? (sign ? -Infinity : Infinity) : NaN;
    } else {
      return (
        (sign ? -1 : 1) *
        Math.pow(2, exponent - 15) *
        (1 + fraction / Math.pow(2, 10))
      );
    }
  }

  readBytes(length: number): Uint8Array {
    const arr = new Uint8Array(
      this.view.buffer,
      this.view.byteOffset + this.offset,
      length
    );
    this.offset += length;
    return arr;
  }
}
