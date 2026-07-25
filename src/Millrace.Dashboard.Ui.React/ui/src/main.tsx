import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { useRoute } from './shared'
import { Instances, JobDetail, Jobs, Overview, Recurring } from './pages'
import './styles.css'

const tabs = [
  { href: '#/', label: 'Overview', match: (r: string) => r === '/' },
  { href: '#/jobs', label: 'Jobs', match: (r: string) => r.startsWith('/jobs') },
  { href: '#/recurring', label: 'Recurring', match: (r: string) => r.startsWith('/recurring') },
  { href: '#/instances', label: 'Workflows', match: (r: string) => r.startsWith('/instances') },
]

function App() {
  const route = useRoute()
  const jobMatch = /^\/jobs\/([^/]+)$/.exec(route)

  return (
    <div className="layout">
      <header className="masthead">
        <h1>Millrace</h1>
        <span className="sub">Durable jobs and workflow orchestration</span>
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
