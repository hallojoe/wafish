async page => {
  const result = await page.evaluate(async () => {
    await DashboardApi.endpoints.saveCluster('browser-transaction', { destinations: { one: { address: 'http://127.0.0.1:9/' } } });
    await DashboardApi.endpoints.saveRoute('browser-transaction', { clusterId: 'browser-transaction', match: { path: '/browser-transaction' } });
    try {
      await DashboardApi.endpoints.saveRoute('browser-invalid', { clusterId: 'missing-cluster', match: { path: '/browser-invalid' } });
      return { failed: 'Invalid route was accepted' };
    } catch (error) {
      return { status: error.status, message: error.message };
    }
  });
  if (result.status !== 'ValidationRejected' || !result.message.includes('missing cluster')) throw new Error(JSON.stringify(result));
  console.log(JSON.stringify(result));
}
