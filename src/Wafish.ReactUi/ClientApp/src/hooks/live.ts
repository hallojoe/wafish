import { useEffect, useState } from "react";
import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { authHeaders, config } from "@/api/client";
export function useLive(
  hub: "overview" | "traffic",
  paused: boolean,
  routeIds: string[] = [],
) {
  const [state, setState] = useState("Connecting");
  const client = useQueryClient();
  const routes = JSON.stringify(routeIds);
  useEffect(() => {
    if (paused) return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const connection = new HubConnectionBuilder()
      .withUrl(`${config.apiBase}/hubs/${hub}`, {
        headers: authHeaders(),
        withCredentials: !["ApiKey", "BasicAuth"].includes(config.authMode),
        ...(Object.keys(authHeaders()).length
          ? { transport: HttpTransportType.LongPolling }
          : {}),
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();
    const subscribe = async () => {
      if (hub === "traffic")
        await connection.invoke("SubscribeToRoutes", JSON.parse(routes));
      if (!disposed) setState("Connected");
    };
    connection.on("OverviewUpdate", (data) =>
      client.setQueryData(["api/overview/snapshot"], data),
    );
    connection.on("TrafficUpdate", () => {
      void client.invalidateQueries({
        queryKey: ["api/traffic/stats?minutes=60"],
      });
    });
    connection.onreconnecting(() => {
      if (!disposed) setState("Reconnecting");
    });
    connection.onreconnected(() => {
      void subscribe().catch(() => {
        if (!disposed) setState("Disconnected");
      });
    });
    const start = async () => {
      if (disposed) return;
      setState("Connecting");
      try {
        await connection.start();
        if (disposed) {
          await connection.stop();
          return;
        }
        await subscribe();
      } catch {
        if (!disposed) {
          setState("Disconnected");
          timer = setTimeout(() => void start(), 5000);
        }
      }
    };
    connection.onclose(() => {
      if (!disposed) {
        setState("Disconnected");
        timer = setTimeout(() => void start(), 5000);
      }
    });
    void start();
    return () => {
      disposed = true;
      clearTimeout(timer);
      void connection.stop();
    };
  }, [hub, paused, routes, client]);
  return paused ? "Paused" : state;
}
