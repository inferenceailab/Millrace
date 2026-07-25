import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { useAsync, useRoute } from './shared'
import { Instances, JobDetail, Jobs, Overview, Recurring, Signals } from './pages'
import { api } from '../../../ui-shared/api'
// Shared with the Angular UI: one stylesheet, so the two dashboards stay the same product
// (src/ui-shared/README.md).
import '../../../ui-shared/millrace.css'

const tabs = [
  { href: '#/', label: 'Overview', match: (r: string) => r === '/' },
  { href: '#/jobs', label: 'Jobs', match: (r: string) => r.startsWith('/jobs') },
  { href: '#/recurring', label: 'Recurring', match: (r: string) => r.startsWith('/recurring') },
  { href: '#/instances', label: 'Workflows', match: (r: string) => r.startsWith('/instances') },
  { href: '#/signals', label: 'Signals', match: (r: string) => r.startsWith('/signals') },
]

function App() {
  const route = useRoute()
  const jobMatch = /^\/jobs\/([^/]+)$/.exec(route)
  // The backend's own report, so the header names the version and provider actually running rather
  // than the one this bundle was built against.
  const { data: info } = useAsync(() => api.info(), [])

  return (
    <div className="layout">
      <header className="masthead">
        <h1>Millrace</h1>
        <span className="sub">Durable jobs and workflow orchestration</span>
        {info && (
          <span className="sub">
            {info.apiVersion} · {info.storageProvider}
          </span>
        )}
      </header>

      <nav className="tabs">
        {tabs.map((tab) => (
          <a key={tab.href} href={tab.href} aria-current={tab.match(route) ? 'page' : undefined}>
            {tab.label}
          </a>
        ))}
      </nav>

      <main>
        {jobMatch ? (
          <JobDetail id={jobMatch[1]!} />
        ) : route.startsWith('/jobs') ? (
          <Jobs />
        ) : route.startsWith('/recurring') ? (
          <Recurring />
        ) : route.startsWith('/instances') ? (
          <Instances />
        ) : route.startsWith('/signals') ? (
          <Signals />
        ) : (
          <Overview />
        )}
      </main>
    </div>
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
