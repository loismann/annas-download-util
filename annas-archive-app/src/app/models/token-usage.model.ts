export interface TokenUsage {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  allowance: number | null;
  allowanceUsedPercent: number | null;
  tokensRemaining: number | null;
  resetsAtUtc: string | null;
  totalCostUsd: number | null;
}

export interface UserTokenUsage {
  userId: string;
  displayName: string;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  totalCostUsd: number;
  allowanceUsd: number;
  allowanceUsedPercent: number;
  resetsAtUtc: string;
  isOverLimit: boolean;
}
