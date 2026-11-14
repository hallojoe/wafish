import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api, write } from "@/api/client";
import { toast } from "sonner";
import { useLocale } from "@/locales/i18n";
export function useApi<T = unknown>(
  path: string,
  enabled = true,
  interval: number | false = false,
) {
  const { locale } = useLocale();
  return useQuery({
    queryKey: [path, locale],
    queryFn: ({ signal }) =>
      api<T>(path, { signal, headers: { "X-Gateway-Locale": locale } }),
    enabled,
    refetchInterval: interval,
  });
}
export function useWrite() {
  const client = useQueryClient();
  const { t } = useLocale();
  return useMutation({
    mutationFn: ({
      path,
      method,
      body,
    }: {
      path: string;
      method: string;
      body?: unknown;
    }) => write(path, method, body),
    onSettled: () => {
      void client.invalidateQueries();
    },
    onSuccess: () => {
      toast.success(t("Applied"));
    },
  });
}
