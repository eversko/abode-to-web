import { describe, expect, it, vi } from 'vitest'
import { HttpAbodeToWebApiClient } from './client'

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {
  status,
  headers: { 'Content-Type': 'application/json' },
})

const requestPath = (input: RequestInfo | URL) => typeof input === 'string'
  ? input
  : input instanceof URL ? input.href : input.url

const systemStatus = {
  observedAt: '2026-07-14T20:00:00Z',
  pc: { condition: 'healthy', label: 'Online' },
  abodeToWeb: { condition: 'healthy', label: 'Healthy' },
  gateway: { condition: 'healthy', label: 'Healthy' },
  cloudflareTunnel: { condition: 'healthy', label: 'Connected' },
  runningProjects: '2', unhealthyProjects: 0, updatesAvailable: '1', cpuPercent: '18.5',
  memoryUsedBytes: '1024', memoryTotalBytes: '2048', diskFreeBytes: '4096', diskTotalBytes: '8192', quietMode: false,
}

describe('HTTP API client', () => {
  it('composes the overview from the server endpoints and normalizes numeric values', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const path = requestPath(input)
      if (path.endsWith('/system/status')) return Promise.resolve(json(systemStatus))
      return Promise.resolve(json([]))
    })
    vi.stubGlobal('fetch', fetchMock)

    const overview = await new HttpAbodeToWebApiClient().getOverview()

    expect(overview.status.runningProjects).toBe(2)
    expect(overview.status.cpuPercent).toBe(18.5)
    expect(fetchMock.mock.calls.map(([input]) => requestPath(input))).toEqual([
      '/api/v1/system/status', '/api/v1/projects', '/api/v1/deployments', '/api/v1/activity',
    ])
  })

  it('fetches a CSRF token and uses the concrete project action route', async () => {
    const fetchMock = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(json({ token: 'csrf-token' }))
      .mockResolvedValueOnce(json({ operationId: 'operation-1', kind: 'start', progressUrl: '/api/v1/operations/operation-1' }, 202))
    vi.stubGlobal('fetch', fetchMock)

    await new HttpAbodeToWebApiClient().runProjectAction('sample-static', 'start')

    expect(fetchMock.mock.calls[0]?.[0] ? requestPath(fetchMock.mock.calls[0][0]) : undefined).toBe('/api/v1/security/csrf')
    expect(fetchMock.mock.calls[1]?.[0] ? requestPath(fetchMock.mock.calls[1][0]) : undefined).toBe('/api/v1/projects/sample-static/start')
    const headers = new Headers(fetchMock.mock.calls[1]?.[1]?.headers)
    expect(headers.get('X-CSRF-TOKEN')).toBe('csrf-token')
  })

  it('maps update checks and logs to the actual server routes', async () => {
    const fetchMock = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(json({ token: 'csrf-token' }))
      .mockResolvedValueOnce(json({ operationId: 'operation-2', kind: 'check-for-updates', progressUrl: '/api/v1/operations/operation-2' }, 202))
    vi.stubGlobal('fetch', fetchMock)
    const client = new HttpAbodeToWebApiClient()

    await client.runProjectAction('sample-static', 'check-for-updates')

    expect(fetchMock.mock.calls[1]?.[0] ? requestPath(fetchMock.mock.calls[1][0]) : undefined).toBe('/api/v1/projects/sample-static/git/check')
    expect(client.getLogDownloadUrl('sample-static', { sources: ['runtime'], minimumSeverity: 'warning' }))
      .toBe('/api/v1/logs/download?source=runtime&severity=warning&projectId=sample-static&limit=2000')
    expect(client.getLogDownloadUrl(undefined, { sources: ['system'], minimumSeverity: 'information' }))
      .toBe('/api/v1/logs/download?source=system&severity=information&limit=2000')
  })

  it('uses the generated mutation contracts for hostname, removal, and operating modes', async () => {
    const accepted = { operationId: 'operation-3', kind: 'change-hostname', progressUrl: '/api/v1/operations/operation-3' }
    const fetchMock = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(json({ token: 'csrf-token' }))
      .mockImplementation(() => Promise.resolve(json(accepted, 202)))
    vi.stubGlobal('fetch', fetchMock)
    const client = new HttpAbodeToWebApiClient()

    await client.changeHostname('sample static', 'next.example.com')
    await client.deleteProject('sample-static', true)
    await client.setQuietMode(false)
    await client.startShowcase(['sample-static', 'sample-docs'])

    expect(client.capabilities).toEqual({ changeHostname: true, changeVisibility: true, deleteProject: true })
    expect(requestPath(fetchMock.mock.calls[1][0])).toBe('/api/v1/projects/sample%20static/hostname')
    expect(fetchMock.mock.calls[1][1]).toMatchObject({ method: 'PUT', body: '{"hostname":"next.example.com"}' })
    expect(requestPath(fetchMock.mock.calls[2][0])).toBe('/api/v1/projects/sample-static?removeRepository=true')
    expect(fetchMock.mock.calls[2][1]).toMatchObject({ method: 'DELETE' })
    expect(requestPath(fetchMock.mock.calls[3][0])).toBe('/api/v1/modes/quiet')
    expect(fetchMock.mock.calls[3][1]).toMatchObject({ method: 'POST', body: '{"enabled":false}' })
    expect(requestPath(fetchMock.mock.calls[4][0])).toBe('/api/v1/modes/showcase')
    expect(fetchMock.mock.calls[4][1]).toMatchObject({ method: 'POST', body: '{"projectIds":["sample-static","sample-docs"]}' })
    for (const call of fetchMock.mock.calls.slice(1)) {
      expect(new Headers(call[1]?.headers).get('X-CSRF-TOKEN')).toBe('csrf-token')
    }
  })
})
