/**
 * Response from SLUM (status/uptime monitor) health check.
 */
export interface SlumHealthEntry {
  name: string;
  health: string;
  cert_exp: string;
}

/**
 * Response from mirror health check.
 */
export interface MirrorHealthEntry {
  extension: string;
  health: number;
}

/**
 * Health check response types.
 */
export type SlumHealthResponse = SlumHealthEntry[];
export type MirrorHealthResponse = MirrorHealthEntry[];
