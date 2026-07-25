import { useEffect, useRef, useState } from 'react'
import type { MillraceJobs } from '../../element/src/millrace-jobs'
import '../../element/src/millrace-jobs'

/**
 * Hosting `<millrace-jobs>` in React 19. THIS FILE IS THE MEASUREMENT.
 *
 * Everything a React consumer must write is here: 3 lines of glue (the ref, the listener, the
 * cleanup), and the extra column is 4 lines. Compare Blazor, which needs a JS file, an args class
 * and an `[EventHandler]` registration for the event alone, and cannot do the column at all.
 */
export function App() {
  const ref = useRef<MillraceJobs>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [state, setState] = useState('')

  // GLUE: React 19 assigns properties to custom elements, but does not map an `onJobSelect` prop to
  // a `job-select` CustomEvent listener. A ref is still required — better than React 18, not absent.
  useEffect(() => {
    const element = ref.current
    if (!element) return
    const onSelect = (e: Event) => setSelected((e as CustomEvent<{ id: string }>).detail.id)
    element.addEventListener('job-select', onSelect)
    return () => element.removeEventListener('job-select', onSelect)
  }, [])

  return (
    <div className="layout">
      <header className="masthead">
        <h1>Millrace</h1>
        <span className="sub">React host — web-component spike (#86)</span>
      </header>

      <div className="controls">
        <div className="field">
          <label htmlFor="host-state">State (set from React)</label>
          <select id="host-state" value={state} onChange={(e) => setState(e.target.value)}>
            <option value="">Any</option>
            <option value="Succeeded">Succeeded</option>
            <option value="Dead">Dead</option>
          </select>
        </div>
      </div>

      <millrace-jobs
        ref={ref}
        base="/millrace/api/v1"
        job-state={state}
        page-size={10}
        /*
          The extension point Blazor cannot reach: a render function, passed as a property. React 19
          assigns it because the element declares `extraColumns`, so this is genuinely 4 lines.
        */
        extraColumns={[
          { header: 'Failures', numeric: true, render: (job) => String(job.failures) },
        ]}
      />

      {selected && <p>Selected job: <code>{selected}</code> — routed by React, not by the element.</p>}
    </div>
  )
}

declare module 'react' {
  namespace JSX {
    interface IntrinsicElements {
      // GLUE: the element's own types do not reach JSX. React's custom-element support is runtime;
      // the type declaration is still the consumer's problem, and `extraColumns` is only typed here
      // because this file declares it.
      'millrace-jobs': React.DetailedHTMLProps<React.HTMLAttributes<MillraceJobs>, MillraceJobs> & {
        ref?: React.Ref<MillraceJobs>
        base?: string
        'job-state'?: string
        'page-size'?: number
        extraColumns?: MillraceJobs['extraColumns']
      }
    }
  }
}
