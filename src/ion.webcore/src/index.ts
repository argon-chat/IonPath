import "./stdFormatters/base.formatters";
import "./stdFormatters/float.formatters";
import "./stdFormatters/signed.formatters";
import "./stdFormatters/unsigned.formatters";

export * from "./baseTypes";
export * from "./cbor";

import { IonFormatterStorage } from "./logic/IonFormatter";

export { IonMaybe, IonArray } from "./logic/IonFormatter";

export interface IIonService {}
export interface IIonUnion<T extends IIonUnion<T>> {}

export { IonFormatterStorage };

export { IonWsClient } from "./ws/IonWsClient";
export {
  IonCallContext,
  IonClientContext,
  IonContentType,
  IonInterceptor,
  IonProtocolError,
  IonRequest,
  IonRequestException,
} from "./unary/IonUnaryRequest";

export interface IServerInteraction extends IIonService
{
  GetTestUnit(i: number): AsyncIterator<number>;
}


export class ServiceExecutor<T extends IIonService> {

}