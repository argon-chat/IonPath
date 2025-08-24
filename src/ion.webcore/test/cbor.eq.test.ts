import { describe, expect, it } from "vitest";
import { CborReader, CborWriter } from "../src";
import { decode, encode } from 'cbor-x';

describe("decodeTest", () => {
  it("1", () => {
    let writer = new CborWriter();

    writer.writeStartArray(3);
    writer.writeBoolean(true);
    writer.writeInt64(0n);
    writer.writeTextString("12345");
    writer.writeEndArray();


    let result = decode(writer.data);
    let encoded = encode(result);


    let reader = new CborReader(encoded);

    const len = reader.readStartArray();
    const b1 = reader.readBoolean();
    const b2 = reader.readInt64();
    const s3 = reader.readTextString();
    reader.readEndArray();


    expect(3).toEqual(len);
    expect(true).toEqual(b1);
    expect(b2).toEqual(0n);
    expect(s3).toEqual("12345");
  });
});
