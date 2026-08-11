/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#include "tick_scheduler.h"

namespace counterstrikesharp {

void TickScheduler::schedule(int tick, std::function<void()> callback)
{
    std::lock_guard<std::mutex> lock(taskMutex);
    scheduledTasks.push(std::make_pair(tick, callback));
}

void TickScheduler::getCallbacks(int currentTick, std::vector<std::function<void()>>& out)
{
    out.clear();

    std::lock_guard<std::mutex> lock(taskMutex);

    // Process tasks due for the current tick.
    //
    // const_cast + move: priority_queue::top() hands back a const reference because
    // mutating a live element would break the heap invariant. Moving out of the element
    // we are about to pop() cannot -- the comparator only looks at .first (the tick),
    // which we leave alone, and the element is destroyed immediately after. This turns
    // a std::function COPY per due task (which allocates for any callback whose captured
    // state exceeds the small-buffer optimisation) into a pointer steal.
    while (!scheduledTasks.empty() && scheduledTasks.top().first <= currentTick)
    {
        out.push_back(std::move(const_cast<std::function<void()>&>(scheduledTasks.top().second)));
        scheduledTasks.pop();
    }
}
} // namespace counterstrikesharp
