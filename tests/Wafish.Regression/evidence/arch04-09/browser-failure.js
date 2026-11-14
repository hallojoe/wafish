async page => {
  const result = await page.evaluate(async () => {
    try {
      await DashboardApi.endpoints.saveCluster('browser-failure', { destinations: { one: { address: 'http://127.0.0.1:9/' } } });
      return { failed: 'Failed write was accepted' };
    } catch (error) { return { status: error.status, message: error.message, stored: error.persistedVersion, active: error.activeVersion }; }
  });
  if (result.status !== 'StorageFailed' || result.stored !== result.active) throw new Error(JSON.stringify(result));
  console.log(JSON.stringify(result));
}
