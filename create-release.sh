#!/bin/bash

set -e

DRY_RUN=false
BETA=false
NO_LOCAL=false

# Parse command line arguments
for arg in "$@"; do
    case $arg in
        --dry-run|-d)
            DRY_RUN=true
            echo "Running in DRY-RUN mode - no changes will be pushed"
            shift
            ;;
        --beta|-b)
            BETA=true
            echo "Running in BETA mode - will create pre-release version"
            shift
            ;;
        --no-local|-n)
            NO_LOCAL=true
            echo "Skipping local Linux build via act - only Windows will be built (by GitHub Actions)"
            shift
            ;;
        *)
            ;;
    esac
done

# Runs the Linux + managed build locally via act and appends the Linux zips
# to the GitHub release. GitHub Actions handles the Windows half on tag push
# (.github/workflows/build-windows.yml), so this covers the remaining half.
run_local_linux_build() {
    local tag="$1"

    if [ "$NO_LOCAL" = true ]; then
        echo "Skipping local Linux build (--no-local)."
        echo "Reminder: run it later with:"
        echo "   act workflow_dispatch -W .github/workflows/build-and-publish.yml \\"
        echo "       --input confirm_local=LOCAL \\"
        echo "       -P ubuntu-latest=catthehacker/ubuntu:full-latest \\"
        echo "       --artifact-server-path /tmp/act-artifacts \\"
        echo "       -s GITHUB_TOKEN=\$(gh auth token)"
        return 0
    fi

    if ! command -v act &> /dev/null; then
        echo "⚠️  act not installed - skipping local Linux build."
        echo "   Install: https://github.com/nektos/act"
        echo "   Then run the command printed above to append Linux zips to $tag."
        return 0
    fi

    if ! docker info &> /dev/null; then
        echo "⚠️  Docker not running - skipping local Linux build."
        echo "   Start Docker and run act manually to append Linux zips to $tag."
        return 0
    fi

    if ! command -v gh &> /dev/null; then
        echo "⚠️  gh CLI not found - cannot fetch GITHUB_TOKEN for act."
        return 0
    fi

    local token
    token=$(gh auth token 2>/dev/null || true)
    if [ -z "$token" ]; then
        echo "⚠️  gh auth token returned empty - run 'gh auth login' first."
        return 0
    fi

    local artifact_dir
    artifact_dir=$(mktemp -d -t act-artifacts-XXXXXX)

    echo ""
    echo "Running local Linux build via act (this takes a few minutes)..."
    echo "   Artifacts: $artifact_dir"
    echo ""

    if act workflow_dispatch \
        -W .github/workflows/build-and-publish.yml \
        --input confirm_local=LOCAL \
        -P ubuntu-latest=catthehacker/ubuntu:full-latest \
        --artifact-server-path "$artifact_dir" \
        -s GITHUB_TOKEN="$token"; then
        echo "✅ Linux zips appended to release $tag"
    else
        echo "⚠️  act run failed. The GitHub release exists; Windows zips will still"
        echo "   be uploaded by the GitHub workflow. Re-run act manually to add Linux."
    fi

    rm -rf "$artifact_dir"
    return 0
}

echo "Starting automated release process..."

echo "Fetching latest tags from remote..."
git fetch --tags

# Get all tags, sorted by version
LATEST_TAG=$(git describe --tags --abbrev=0 2>/dev/null || echo "v1.0.0")
echo "Latest tag found: $LATEST_TAG"

# Parse version from tag (handling both beta and stable versions)
if [[ $LATEST_TAG =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)(-beta)?$ ]]; then
    MAJOR=${BASH_REMATCH[1]}
    MINOR=${BASH_REMATCH[2]}
    PATCH=${BASH_REMATCH[3]}
    IS_BETA=${BASH_REMATCH[4]}
else
    echo "Error: Could not parse version from tag $LATEST_TAG"
    echo "Expected format: v1.0.x or v1.0.x-beta (e.g., v1.0.355 or v1.0.356-beta)"
    exit 1
fi

# Determine new version based on beta flag
if [ "$BETA" = true ]; then
    if [ -n "$IS_BETA" ]; then
        # Current is beta, can't create another beta of same version
        echo "Error: Current version $LATEST_TAG is already a beta"
        echo "To release stable version, run without --beta flag"
        exit 1
    fi
    # Create beta of next version
    NEW_PATCH=$((PATCH + 1))
    NEW_TAG="v$MAJOR.$MINOR.$NEW_PATCH-beta"
    RELEASE_TYPE="pre-release (beta)"
else
    if [ -n "$IS_BETA" ]; then
        # Current is beta, promote to stable (same version, remove -beta)
        NEW_PATCH=$PATCH
        NEW_TAG="v$MAJOR.$MINOR.$NEW_PATCH"
        RELEASE_TYPE="stable release (promoted from beta)"
    else
        # Current is stable, create next stable
        NEW_PATCH=$((PATCH + 1))
        NEW_TAG="v$MAJOR.$MINOR.$NEW_PATCH"
        RELEASE_TYPE="stable release"
    fi
fi

echo "New version will be: $NEW_TAG ($RELEASE_TYPE)"

# Handle changelog generation differently for beta promotion
if [ -n "$IS_BETA" ] && [ "$BETA" = false ]; then
    # Promoting beta to stable - update existing changelog entry
    echo "Updating changelog entry from beta to stable..."
    
    # Remove beta warning from changelog
    sed -i "/⚠️ \*\*BETA PRE-RELEASE\*\*/d" CHANGELOG.md
    
    # Update the version tag in changelog (remove -beta suffix)
    sed -i "s/\[$LATEST_TAG\]/[$NEW_TAG]/" CHANGELOG.md
    
    # Update the comparison link if it exists
    sed -i "s/$LATEST_TAG/$NEW_TAG/g" CHANGELOG.md
    
    echo "Changelog updated: $LATEST_TAG -> $NEW_TAG (beta warning removed)"
else
    # Normal changelog generation for new versions
    echo "Generating changelog with git-cliff..."
    npx git-cliff -o CHANGELOG.md -t "$NEW_TAG"
    
    # Add beta indicator to changelog if this is a beta release
    if [ "$BETA" = true ]; then
        # Add beta warning at the top of the new release section
        sed -i "/## \[$NEW_TAG\]/a \\
\\
⚠️ **BETA PRE-RELEASE** - This version is for testing purposes only\\
" CHANGELOG.md
    fi
fi

if ! git diff --quiet CHANGELOG.md; then
    echo "Changelog updated successfully"
    
    git add CHANGELOG.md
    
    # Create appropriate commit message
    if [ "$BETA" = true ]; then
        COMMIT_MSG="release: $NEW_TAG (beta pre-release)"
    else
        if [ -n "$IS_BETA" ]; then
            COMMIT_MSG="release: $NEW_TAG (promoted from beta)"
        else
            COMMIT_MSG="release: $NEW_TAG"
        fi
    fi
    
    echo "Committing changelog with message: $COMMIT_MSG"
    
    if [ "$DRY_RUN" = true ]; then
        git commit -m "$COMMIT_MSG"
        
        echo "Creating tag locally: $NEW_TAG"
        git tag "$NEW_TAG"
        
        echo "DRY-RUN: Would push commit to remote"
        echo "DRY-RUN: Would push tag to remote"
        if [ "$BETA" = true ]; then
            echo "DRY-RUN: Would mark as pre-release on GitHub"
        fi
    else
        git commit -m "$COMMIT_MSG"
        
        echo "Pushing commit to remote..."
        git push origin $(git branch --show-current)
        
        echo "Creating and pushing tag: $NEW_TAG"
        git tag "$NEW_TAG"
        git push origin tag "$NEW_TAG"
        
        if [ "$BETA" = true ]; then
            echo ""
            echo "Creating GitHub pre-release..."
            
            # Check if gh CLI is installed
            if command -v gh &> /dev/null; then
                if gh release create "$NEW_TAG" \
                    --prerelease \
                    --title "$NEW_TAG - Beta Pre-release" \
                    --notes "⚠️ **BETA PRE-RELEASE** - This version is for testing purposes only

## Feedback
Please report any issues or feedback before we promote this to stable release."; then
                    echo "✅ Pre-release created successfully on GitHub!"
                else
                    echo "⚠️ Failed to create pre-release. Please create it manually:"
                    echo "   gh release create $NEW_TAG --prerelease --generate-notes"
                fi
            else
                echo "⚠️ GitHub CLI (gh) not found. Install it to auto-create pre-releases:"
                echo "   https://cli.github.com/"
                echo ""
                echo "Manual steps:"
                echo "1. Go to https://github.com/mrc4tt/CounterStrikeSharp/releases/new"
                echo "2. Select tag: $NEW_TAG"
                echo "3. Check 'Set as a pre-release' checkbox"
                echo "4. Publish release"
            fi
        else
            echo ""
            echo "Creating GitHub release..."
            
            # Check if gh CLI is installed
            if command -v gh &> /dev/null; then
                if gh release create "$NEW_TAG" \
                    --title "$NEW_TAG" \
                    --notes "✅ **STABLE RELEASE**

## Installation
Download the latest stable build from the assets below."; then
                    echo "✅ Release created successfully on GitHub!"
                else
                    echo "⚠️ Failed to create release. Please create it manually:"
                    echo "   gh release create $NEW_TAG --generate-notes"
                fi
            else
                echo "⚠️ GitHub CLI (gh) not found. Skipping automatic release creation."
                echo "   Install it from: https://cli.github.com/"
            fi
        fi

        # GitHub Actions is now handling the Windows build (build-windows.yml);
        # run act locally to build Linux + managed and append to the same release.
        run_local_linux_build "$NEW_TAG"
    fi

    echo ""
    echo "=========================================="
    echo "Release $NEW_TAG completed successfully!"
    echo "=========================================="
    echo "Summary:"
    echo "   - Previous version: $LATEST_TAG"
    echo "   - New version: $NEW_TAG"
    echo "   - Release type: $RELEASE_TYPE"
    echo "   - Changelog updated: Yes"
    if [ "$DRY_RUN" = true ]; then
        echo "   - Commit pushed: (dry-run)"
        echo "   - Tag created and pushed: (dry-run)"
    else
        echo "   - Commit pushed: Yes"
        echo "   - Tag created and pushed: Yes"
        echo "   - Windows build: running on GitHub Actions (build-windows.yml)"
        if [ "$NO_LOCAL" = true ]; then
            echo "   - Linux build: SKIPPED (run act manually to append)"
        else
            echo "   - Linux build: handled locally via act"
        fi
    fi

    if [ "$BETA" = true ]; then
        echo ""
        echo "Next steps:"
        echo "   1. Mark release as pre-release on GitHub"
        echo "   2. Test beta version thoroughly"
        echo "   3. When ready, run: ./create-release.sh (without --beta) to promote to stable"
    fi
elif [ -n "$IS_BETA" ] && [ "$BETA" = false ]; then
    # Promoting beta to stable without changelog changes - just create tag
    echo "No changelog changes needed for beta promotion (same commit)"
    echo "Creating stable tag on the same commit as beta..."
    
    if [ "$DRY_RUN" = true ]; then
        echo "Creating tag locally: $NEW_TAG"
        git tag "$NEW_TAG"
        echo "DRY-RUN: Would push tag to remote"
    else
        echo "Creating and pushing tag: $NEW_TAG"
        git tag "$NEW_TAG"
        git push origin tag "$NEW_TAG"
        
        echo ""
        echo "Creating GitHub release..."
        
        # Check if gh CLI is installed
        if command -v gh &> /dev/null; then
            if gh release create "$NEW_TAG" \
                --title "$NEW_TAG" \
                --notes "✅ **STABLE RELEASE** (promoted from $LATEST_TAG)

## Installation
Download the latest stable build from the assets below."; then
                echo "✅ Release created successfully on GitHub!"
            else
                echo "⚠️ Failed to create release. Please create it manually:"
                echo "   gh release create $NEW_TAG --generate-notes"
            fi
        else
            echo "⚠️ GitHub CLI (gh) not found. Skipping automatic release creation."
            echo "   Install it from: https://cli.github.com/"
        fi

        run_local_linux_build "$NEW_TAG"
    fi

    echo ""
    echo "=========================================="
    echo "Release $NEW_TAG completed successfully!"
    echo "=========================================="
    echo "Summary:"
    echo "   - Previous version: $LATEST_TAG (beta)"
    echo "   - New version: $NEW_TAG (stable)"
    echo "   - Release type: $RELEASE_TYPE"
    echo "   - Same commit: Yes"
    if [ "$DRY_RUN" = true ]; then
        echo "   - Tag created and pushed: (dry-run)"
    else
        echo "   - Tag created and pushed: Yes"
        echo "   - Windows build: running on GitHub Actions (build-windows.yml)"
        if [ "$NO_LOCAL" = true ]; then
            echo "   - Linux build: SKIPPED (run act manually to append)"
        else
            echo "   - Linux build: handled locally via act"
        fi
    fi
else
    echo "No changes detected in CHANGELOG.md"
    echo "This might indicate that there are no new commits since the last release."
    exit 1
fi
