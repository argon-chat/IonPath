/**
 * Helper utilities for managing correlation IDs across a logical operation/flow.
 *
 * Usage:
 *   const correlationId = IonCorrelation.create();
 *   // Pass to all calls within an operation...
 *   ctx.correlationId = correlationId;
 */
export const IonCorrelation = {
  /** Generate a new correlation ID (UUID v4). */
  create(): string {
    return crypto.randomUUID();
  },
};

/**
 * Creates an IonClientContext with an auto-generated session ID.
 */
export function createIonClientContext(
  baseUrl: string,
  interceptors: import("../unary/IonUnaryRequest").IonInterceptor[] = []
): import("../unary/IonUnaryRequest").IonClientContext {
  return {
    baseUrl,
    interceptors,
    sessionId: crypto.randomUUID(),
  };
}
