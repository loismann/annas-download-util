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
