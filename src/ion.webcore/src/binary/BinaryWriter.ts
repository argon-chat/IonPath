export class BinaryWriter {
  private buffer: Uint8Array;
  private offset: number = 0;

  constructor(size: number = 32) {
    this.buffer = new Uint8Array(size);
  }

  get data(): Uint8Array {
    return this.buffer.subarray(0, this.offset);
  }

  get position(): number {
    return this.offset;
  }

  seek(position: number) {
    this.offset = position;
  }

  private ensure(size: number) {
    if (this.offset + size > this.buffer.length) {
      const newBuffer = new Uint8Array(this.buffer.length * 2 + size);
      newBuffer.set(this.buffer);
      this.buffer = newBuffer;
    }
  }

  writeUint8(value: number) {
    this.ensure(1);
    this.buffer[this.offset++] = value & 0xff;
  }

  writeInt8(value: number) {
    this.ensure(1);
    this.buffer[this.offset++] = value & 0xff;
  }

  writeUint16(value: number, littleEndian = true) {
    this.ensure(2);
    if (littleEndian) {
      this.buffer[this.offset++] = value & 0xff;
      this.buffer[this.offset++] = (value >> 8) & 0xff;
    } else {
      this.buffer[this.offset++] = (value >> 8) & 0xff;
      this.buffer[this.offset++] = value & 0xff;
    }
  }

  writeInt16(value: number, littleEndian = true) {
    this.writeUint16(value, littleEndian);
  }

  writeUint32(value: number, littleEndian = true) {
    this.ensure(4);
    if (littleEndian) {
      this.buffer[this.offset++] = value & 0xff;
      this.buffer[this.offset++] = (value >> 8) & 0xff;
      this.buffer[this.offset++] = (value >> 16) & 0xff;
      this.buffer[this.offset++] = (value >> 24) & 0xff;
    } else {
      this.buffer[this.offset++] = (value >> 24) & 0xff;
      this.buffer[this.offset++] = (value >> 16) & 0xff;
      this.buffer[this.offset++] = (value >> 8) & 0xff;
      this.buffer[this.offset++] = value & 0xff;
    }
  }

  writeInt32(value: number, littleEndian = true) {
    this.writeUint32(value >>> 0, littleEndian);
  }

  writeBigInt64(value: bigint, littleEndian = true) {
    this.ensure(8);
    if (littleEndian) {
      for (let i = 0; i < 8; i++) {
        this.buffer[this.offset++] = Number((value >> BigInt(i * 8)) & 0xffn);
      }
    } else {
      for (let i = 7; i >= 0; i--) {
        this.buffer[this.offset++] = Number((value >> BigInt(i * 8)) & 0xffn);
      }
    }
  }

  writeBigUint64(value: bigint, littleEndian = true) {
    this.writeBigInt64(value, littleEndian);
  }

  writeInt128(value: bigint, littleEndian = true) {
    this.ensure(16);
    if (littleEndian) {
      for (let i = 0; i < 16; i++) {
        this.buffer[this.offset++] = Number((value >> BigInt(i * 8)) & 0xffn);
      }
    } else {
      for (let i = 15; i >= 0; i--) {
        this.buffer[this.offset++] = Number((value >> BigInt(i * 8)) & 0xffn);
      }
    }
  }

  writeFloat32(value: number, littleEndian = true) {
    this.ensure(4);
    const f32 = new Float32Array(1);
    const u8 = new Uint8Array(f32.buffer);
    f32[0] = value;
    if (littleEndian) {
      for (let i = 0; i < 4; i++) this.buffer[this.offset++] = u8[i];
    } else {
      for (let i = 3; i >= 0; i--) this.buffer[this.offset++] = u8[i];
    }
  }

  writeFloat64(value: number, littleEndian = true) {
    this.ensure(8);
    const f64 = new Float64Array(1);
    const u8 = new Uint8Array(f64.buffer);
    f64[0] = value;
    if (littleEndian) {
      for (let i = 0; i < 8; i++) this.buffer[this.offset++] = u8[i];
    } else {
      for (let i = 7; i >= 0; i--) this.buffer[this.offset++] = u8[i];
    }
  }

  writeFloat16(value: number, littleEndian = true) {
    this.ensure(2);
    if (isNaN(value)) {
      this.writeUint16(0x7e00, littleEndian); 
      return;
    }
    if (!isFinite(value)) {
      this.writeUint16(value < 0 ? 0xfc00 : 0x7c00, littleEndian); 
      return;
    }

    const sign = value < 0 ? 1 : 0;
    value = Math.abs(value);

    let exp = Math.floor(Math.log2(value));
    let frac = value / Math.pow(2, exp) - 1;

    let exponent = exp + 15;
    let fraction = Math.round(frac * 1024);

    if (exponent <= 0) {
      fraction = Math.round((value / Math.pow(2, -14)) * 1024);
      exponent = 0;
    } else if (exponent >= 31) {
      exponent = 31;
      fraction = 0;
    }

    const bits = (sign << 15) | (exponent << 10) | (fraction & 0x3ff);
    this.writeUint16(bits, littleEndian);
  }

  writeBytes(bytes: Uint8Array) {
    this.ensure(bytes.length);
    this.buffer.set(bytes, this.offset);
    this.offset += bytes.length;
  }
}
