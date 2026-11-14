import { useEffect } from "react";
import type { ReactNode } from "react";

const english = {
  locale: "en-US",
  t: (key: string) => key,
};

export function LocaleProvider({ children }: { children: ReactNode }) {
  useEffect(() => {
    localStorage.setItem("gateway-locale", english.locale);
    document.documentElement.lang = english.locale;
    document.cookie = `dashboard_locale=${english.locale};path=/;SameSite=Lax`;
  }, []);
  return <>{children}</>;
}

export const useLocale = () => english;
