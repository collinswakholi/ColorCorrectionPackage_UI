"""
Scatter plot utilities for color correction visualization
Extracted from key_functions.py for use with ColorCorrectionPipeline
"""

import numpy as np
import matplotlib
matplotlib.use('Agg')  # Use non-GUI backend for thread safety
import matplotlib.pyplot as plt
from typing import Dict


def poly_func(x, coeffs):
    """
    Polynomial evaluation using Horner's method
    
    Args:
        x: Input values
        coeffs: Polynomial coefficients
        
    Returns:
        Result of polynomial evaluation
    """
    result = 0
    for c in coeffs:
        result = result * x + c
    return result


def estimate_fit(measured: np.ndarray, reference: np.ndarray, degree: int = 3):
    """
    Estimate polynomial fit coefficients
    
    Args:
        measured: Measured values (will be flattened)
        reference: Reference values (will be flattened)
        degree: Degree of polynomial fit
        
    Returns:
        Polynomial coefficients
    """
    measured = measured.flatten()
    reference = reference.flatten()

    # Check for matching dimensions
    if measured.shape != reference.shape:
        raise ValueError("Measured and reference arrays must have the same shape.")

    # Create the design matrix for the specified polynomial degree
    X = np.vstack([measured**i for i in range(degree, -1, -1)]).T

    # Solve for coefficients using lstsq
    coeffs = np.linalg.lstsq(X, reference, rcond=None)[0]

    return coeffs


def scatter_RGB(
    reference: np.ndarray,  # reference values of RGB (24x3)
    mats: Dict[str, np.ndarray],  # dictionary of named matrices, each matrix is 24x3
    point_lw: float = 1.5,
    maker_size: float = 100,
    best_fit: bool = True,
    font_size: float = 14,
    save_=None,
):
    """
    Create RGB scatter plot comparing reference and measured color patches
    
    Args:
        reference: Reference RGB values (24x3 array for ColorChecker)
        mats: Dictionary of named matrices to compare against reference
        point_lw: Line width for point edges
        maker_size: Base size for scatter plot markers
        best_fit: Whether to show best fit curves
        font_size: Font size for labels
        save_: Optional file path to save the plot
        
    Returns:
        None (displays or saves the plot)
    """
    assert reference.shape[1] == 3
    for name, mat in mats.items():
        assert (
            mat.shape == reference.shape
        ), f"Matrix {name} does not have same shape as reference"

    # Visual settings for different datasets
    shapes = ["^", "o", "v", "*", "D", "p"]
    alphas = [1.0, 0.75, 0.5, 0.25, 0.125]
    sizes = maker_size * np.array([1.0, 0.9, 0.8, 0.7, 0.6, 0.5])

    plt.figure(figsize=(10, 10))
    font_headings = int(1.2 * font_size)
    plt.title("RGB Scatter Plot", fontsize=font_headings)
    plt.xlabel("Reference", fontsize=font_headings)
    plt.ylabel("Predicted", fontsize=font_headings)

    for i, (k, v) in enumerate(mats.items()):
        shape_ = shapes[i % len(shapes)]
        alpha_ = alphas[i % len(alphas)]
        size_ = sizes[i % len(sizes)]

        # Ensure colors are in valid range [0, 1]
        colors = np.clip(v, 0, 1)
        edge_colors = 0.25 * colors

        plt.scatter(
            np.mean(reference, axis=1),
            np.mean(v, axis=1),
            marker=shape_,
            color=colors,
            s=size_,
            edgecolor=edge_colors,
            linewidth=point_lw,
            alpha=alpha_,
            label=k,
        )

        if best_fit:
            degree = 1
            coeffs = estimate_fit(reference, v, degree=degree)

            x = np.linspace(0, 1, 10)
            prediction = poly_func(x, coeffs)
            plt.plot(
                x,
                prediction,
                color=[0.25, 0.25, 0.25],
                linestyle="--",
                label=f"{k} Order {degree} Best Fit Curve",
                linewidth=2,
                alpha=alpha_,
            )

    # Plot 1:1 reference line
    plt.plot(
        reference.flatten(),
        reference.flatten(),
        color="k",
        linestyle="-",
        label=f"1:1 Line",
        linewidth=1.5,
    )
    
    plt.legend(fontsize=font_size)
    plt.xticks(fontsize=font_size)
    plt.yticks(fontsize=font_size)
    plt.xlim(0, 1)
    plt.ylim(0, 1)
    plt.box(True)
    plt.tight_layout()
    plt.grid()

    if save_ is not None:
        plt.savefig(save_, dpi=1200)
    
        # Don't call plt.show() in backend - it will be handled by the caller


def create_scatter_plot(original_rgb: np.ndarray, corrected_rgb: np.ndarray, name: str = "image") -> str:
    """
    Create scatter plot comparison and return as base64 encoded image
    
    Args:
        original_rgb: Original image RGB values (H x W x 3)
        corrected_rgb: Corrected image RGB values (H x W x 3)
        name: Image name for labeling
        
    Returns:
        Base64 encoded PNG image of the scatter plot
    """
    import io
    import base64
    
    # Sample pixels to avoid overwhelming the plot (max 5000 points)
    h, w, _ = original_rgb.shape
    total_pixels = h * w
    sample_size = min(5000, total_pixels)
    
    # Random sampling
    indices = np.random.choice(total_pixels, sample_size, replace=False)
    orig_flat = original_rgb.reshape(-1, 3)
    corr_flat = corrected_rgb.reshape(-1, 3)
    
    orig_sample = orig_flat[indices]
    corr_sample = corr_flat[indices]
    
    # Create figure
    plt.figure(figsize=(10, 10))
    
    # Plot for each channel
    colors = ['red', 'green', 'blue']
    labels = ['R', 'G', 'B']
    
    for i, (color, label) in enumerate(zip(colors, labels)):
        plt.scatter(
            orig_sample[:, i],
            corr_sample[:, i],
            c=color,
            alpha=0.3,
            s=20,
            label=f'{label} Channel',
            edgecolors='none'
        )
    
    # Plot 1:1 reference line
    plt.plot([0, 1], [0, 1], 'k--', linewidth=2, label='1:1 Reference')
    
    plt.xlabel('Original RGB Values', fontsize=14)
    plt.ylabel('Corrected RGB Values', fontsize=14)
    plt.title(f'RGB Scatter Plot - {name}', fontsize=16)
    plt.legend(fontsize=12)
    plt.xlim(0, 1)
    plt.ylim(0, 1)
    plt.grid(True, alpha=0.3)
    plt.tight_layout()
    
    # Save to buffer
    buf = io.BytesIO()
    plt.savefig(buf, format='png', dpi=150, bbox_inches='tight')
    buf.seek(0)
    plt.close()
    
    # Encode to base64
    img_base64 = base64.b64encode(buf.read()).decode('utf-8')
    return f"data:image/png;base64,{img_base64}"

