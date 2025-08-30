import { describe, expect, it } from "vitest";
import { CborReader, CborWriter, Guid, IonFormatterStorage } from "../src";
import { decode, encode } from "cbor-x";
import "./../src/index";

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

  it("2", () => {
    function fromBase64(base64: string): Uint8Array {
      return new Uint8Array(Buffer.from(base64, "base64"));
    }

    let result = decode(
      fromBase64(
        "ggCCeQGAZXlKaGJHY2lPaUpJVXpVeE1pSXNJbXRwWkNJNklrRkVSVFJCTWtKQ05rRXlSRU0zTWpVaUxDSjBlWEFpT2lKS1YxUWlmUS5leUpwWkNJNklqVm1OelEyWW1FMUxUQmxOV0V0TkRNek1TMWhZakZrTFdOaE1XSTRaV0ZoT0dReE9DSXNJbTFwWkNJNklqQXhPVGhsWkRneUxUUTRaRGN0TnpZeE1pMDVabU13TFRoaFpqZ3lNakEzTURZM09DSXNJbTVpWmlJNk1UYzFOak0wT0RRNE55d2laWGh3SWpveE56YzJNRFEzTmpnM0xDSnBZWFFpT2pFM05UWXpORGcwT0Rjc0ltbHpjeUk2SWtGeVoyOXVJaXdpWVhWa0lqb2lRWEpuYjI0aWZRLmpuSFRfa1FoNlZQcGtuTHVzcm1KTWhRUzJCZ3VtbEZVUHp4SHhwODRtMG1lbS1fd1dLTllzWHpHRXl5ZDM4a3NrdkFXckhCRk14aHlyYVZtVF8zLU5B9g=="
      )
    );
    let encoded = encode(result);

    console.log(result);

    let reader = new CborReader(encoded);

    const len = reader.readStartArray();
    const b1 = reader.readInt32();
    const arr2 = reader.readStartArray();
    const s3 = reader.readTextString();
    const s4 = reader.readNull();
    reader.readEndArray();
    reader.readEndArray();

    expect(2).toEqual(len);
    expect(0).toEqual(b1);
    expect(arr2).toEqual(2);
  });

  it("3", () => {
    const writer = new CborWriter();

    writer.writeStartArray();
    IonFormatterStorage.get<Guid>("guid").write(
      writer,
      "b7404c69-abf2-4d73-b7b0-f4f232c85815"
    );
    writer.writeEndArray();

    const result = writer.data;
    let decoded = decode(writer.data);

    console.warn(result, decoded);

    const reader = new CborReader(writer.data);

    reader.readStartArray();
    const existGuid = IonFormatterStorage.get<Guid>("guid").read(reader);
    reader.readEndArray();

    console.warn(existGuid);

    expect("b7404c69-abf2-4d73-b7b0-f4f232c85815").toEqual(existGuid);
  });
});
