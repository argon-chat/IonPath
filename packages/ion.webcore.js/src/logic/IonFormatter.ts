import { IonArray } from "../baseTypes";
import { CborReader, CborWriter } from "../cbor";
import { CborReaderState } from "../cbor/CborReader";
import { IonClientContext } from "../unary/IonUnaryRequest";
import { IIonService } from "./IIonService";
import { ServiceExecutor } from "./ServiceExecutor";

export interface IonFormatter<T> {
  read(reader: CborReader): T;
  write(writer: CborWriter, value: T): void;
}
export type FieldSchema = Record<string, string>;

export type ExecutorConstructor<T extends IIonService> = new (
  ctx: IonClientContext,
  signal: AbortSignal
) => ServiceExecutor<T> & T;

export class IonFormatterStorage {
  private static map = new Map<string, IonFormatter<any>>();
  private static mapExecutors = new Map<string, ExecutorConstructor<any>>();

  static register<T>(name: string, formatter: IonFormatter<T>) {
    this.map.set(name, formatter);
  }

  static registerClientExecutor<T extends IIonService>(
    name: string,
    executorCtor: ExecutorConstructor<T>
  ) {
    this.mapExecutors.set(name, executorCtor);
  }

  static createExecutor<T extends IIonService>(
    name: string,
    ctx: IonClientContext,
    signal: AbortSignal
  ): ServiceExecutor<T> & T {
    const ctor = this.mapExecutors.get(name);
    if (!ctor) throw new Error(`Executor not registered: ${name}`);
    return new ctor(ctx, signal);
  }

  static get<T>(name: string): IonFormatter<T> {
    const f = this.map.get(name);
    if (!f) throw new Error(`Formatter not found: ${name}`);
    return f;
  }

  static readMaybe<T>(reader: CborReader, typeName: string): IonMaybe<T> {
    const state = reader.peekState();
    if (state !== CborReaderState.Null) {
      const value = IonFormatterStorage.get<T>(typeName).read(reader);
      return IonMaybe.Some(value);
    }
    reader.readNull();
    return IonMaybe.None<T>();
  }

  static readNullable<T>(reader: CborReader, typeName: string): T | null {
    const state = reader.peekState();
    if (state !== CborReaderState.Null) {
      const value = IonFormatterStorage.get<T>(typeName).read(reader);
      return value;
    }
    reader.readNull();
    return null;
  }

  static writeMaybe<T>(
    writer: CborWriter,
    ionMaybe: IonMaybe<T>,
    typeName: string
  ): void {
    if (!ionMaybe.hasValue) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.get<T>(typeName).write(writer, ionMaybe.value as T);
  }

  static writeNullable<T>(
    writer: CborWriter,
    ionMaybe: T | null,
    typeName: string = ""
  ): void {
    if (ionMaybe === undefined || ionMaybe === null) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.get<T>(typeName).write(writer, ionMaybe as T);
  }

  static readArray<T>(reader: CborReader, typeName: string): IonArray<T> {
    const size = reader.readStartArray();
    if (size === null)
      throw new Error("Indefinite arrays are not supported here");

    const values: T[] = [];
    for (let i = 0; i < size; i++) {
      const val = IonFormatterStorage.get<T>(typeName).read(reader);
      values.push(val);
    }

    reader.readEndArray();
    return values;
  }

  static writeArray<T>(
    writer: CborWriter,
    array: IonArray<T>,
    typeName: string
  ): void {
    writer.writeStartArray(array.length);
    if (array.length === 0) {
      writer.writeEndArray();
      return;
    }

    for (let i = 0; i < array.length; i++) {
      IonFormatterStorage.get<T>(typeName).write(writer, array[i]);
    }

    writer.writeEndArray();
  }

  static makePartialFormatter<T>(
    schema: FieldSchema
  ): IonFormatter<Partial<T>> {
    return {
      read(reader: CborReader): Partial<T> {
        const result: Partial<T> = {};
        const length = reader.readStartMap();

        for (let i = 0; i < (length ?? 0); i++) {
          const key = reader.readTextString();
          const typeName = schema[key];
          if (!typeName) {
            reader.skipValue();
            continue;
          }

          if (reader.peekState() === CborReaderState.Null) {
            reader.readNull();
            (result as any)[key] = null;
          } else {
            const fmt = IonFormatterStorage.get(typeName);
            (result as any)[key] = fmt.read(reader);
          }
        }

        reader.readEndMap();
        return result;
      },

      write(writer: CborWriter, value: Partial<T>): void {
        const entries = Object.entries(value).filter(
          ([_, v]) => v !== undefined
        );
        writer.writeStartMap(entries.length);

        for (const [key, val] of entries) {
          writer.writeTextString(key);

          if (val === null) {
            writer.writeNull();
          } else {
            const typeName = schema[key];
            if (!typeName) {
              throw new Error(`No formatter schema for field '${key}'`);
            }
            const fmt = IonFormatterStorage.get(typeName);
            fmt.write(writer, val as any);
          }
        }

        writer.writeEndMap();
      },
    };
  }
}

export class IonMaybe<T> {
  private constructor(
    public readonly value: T | null,
    public readonly hasValue: boolean
  ) {}

  static Some<T>(value: T): IonMaybe<T> {
    return new IonMaybe(value, true);
  }

  static None<T>(): IonMaybe<T> {
    return new IonMaybe<T>(null as any, false);
  }

  unwrap(): T {
    if (!this.hasValue) throw new Error("Cannot unwrap a None value.");
    return this.value as T;
  }

  static from<T>(value: T | null | undefined): IonMaybe<T> {
    if (value === null || value === undefined) return IonMaybe.None<T>();
    return IonMaybe.Some(value);
  }

  unwrapOrDefault(): T | null {
    return this.value as T | null;
  }
}
export interface IS extends IIonService {
  asdasd: Int16Array;
  aqweqwe: 12;
}
