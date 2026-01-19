export type Guid = string;

export type IonArray<T> = T[];

export type IonBytes = Uint8Array;
export type bytes = Uint8Array;

export interface DateOnly {
  year: number;
  month: number;
  day: number;
}

export interface TimeOnly {
  hour: number;
  minute: number;
  second: number;
  millisecond: number;
  microsecond: number;
}

export interface DateTimeOffset {
  date: Date;
  offsetMinutes: number;
}

export interface Duration {
  ticks: bigint;
}
